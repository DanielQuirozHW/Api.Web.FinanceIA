// Project : Api.Web.FinanceIA
// Created : 2026-11-08

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// Registers the tenant PDF branding: the SSRF-guarded "BrandingLogoClient", the logo fetcher and the per-export
/// provider. One call from Program.cs; a test builds the same registration to prove the handler's settings.
/// </summary>
public static class PdfBrandingServiceRegistration
{
    /// <summary>The whole-request timeout of the logo client (the fetcher's own budget is the same 5 s).</summary>
    internal static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Adds the branding logo client, <see cref="IBrandingLogoFetcher"/> and <see cref="ITenantPdfBrandingProvider"/>.</summary>
    public static IServiceCollection AddTenantPdfBranding(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddMemoryCache();

        // No redirects (a 3xx is a refusal), no proxy (a proxy would make the connect policy check the proxy, not
        // the logo host), no decompression (the 1 MB cap is on the wire bytes), and the connect policy that refuses
        // every non-public address. The factory's default loggers are removed: they log the full request URL at
        // Information, and only the host may be logged.
        services.AddHttpClient(BrandingLogoFetcher.HttpClientName, client =>
            {
                client.Timeout = ClientTimeout;
                client.DefaultRequestHeaders.Add("Accept", "image/png, image/jpeg");
                client.DefaultRequestHeaders.Add("User-Agent", "FinancIA/1.0 (+https://hwapplications.com)");
            })
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
            {
                AllowAutoRedirect              = false,
                UseProxy                       = false,
                UseCookies                     = false,
                AutomaticDecompression         = System.Net.DecompressionMethods.None,
                MaxResponseHeadersLength       = 64,
                ConnectTimeout                 = ClientTimeout,
                ConnectCallback                = BrandingLogoConnectPolicy.ConnectAsync,
            })
            .RemoveAllLoggers();

        services.AddSingleton<IBrandingLogoFetcher, BrandingLogoFetcher>();
        services.AddScoped<ITenantPdfBrandingProvider, TenantPdfBrandingProvider>();
        return services;
    }
}
