// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using System.Net;
using Api.Web.FinanceIA.Servicios.Pdf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfBranding;

/// <summary>The registered "BrandingLogoClient" really carries the SSRF settings the fetcher relies on.</summary>
public sealed class BrandingLogoHttpClientRegistrationTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddTenantPdfBranding();
        return services.BuildServiceProvider();
    }

    private static SocketsHttpHandler PrimaryHandler(HttpMessageHandler handler)
    {
        HttpMessageHandler? current = handler;
        while (current is DelegatingHandler delegating)
            current = delegating.InnerHandler;

        return Assert.IsType<SocketsHttpHandler>(current);
    }

    [Fact]
    public void PrimaryHandler_FollowsNoRedirect_UsesNoProxy_AndHasTheConnectPolicy()
    {
        using ServiceProvider provider = BuildProvider();
        HttpMessageHandler handler = provider.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(BrandingLogoFetcher.HttpClientName);

        SocketsHttpHandler sockets = PrimaryHandler(handler);

        Assert.False(sockets.AllowAutoRedirect);
        Assert.False(sockets.UseProxy);
        Assert.Equal(DecompressionMethods.None, sockets.AutomaticDecompression);
        Assert.NotNull(sockets.ConnectCallback);
    }

    [Fact]
    public void Client_TimesOutAfterFiveSeconds()
    {
        using ServiceProvider provider = BuildProvider();
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(BrandingLogoFetcher.HttpClientName);

        Assert.Equal(TimeSpan.FromSeconds(5), client.Timeout);
    }

    [Fact]
    public void Registration_ResolvesTheFetcherAsSingleton_AndTheProviderAsScoped()
    {
        var services = new ServiceCollection();
        services.AddTenantPdfBranding();

        Assert.Equal(ServiceLifetime.Singleton, services.Single(d => d.ServiceType == typeof(IBrandingLogoFetcher)).Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, services.Single(d => d.ServiceType == typeof(ITenantPdfBrandingProvider)).Lifetime);
    }
}
