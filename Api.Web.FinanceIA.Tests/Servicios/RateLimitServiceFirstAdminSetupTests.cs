// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-25

using System.Text.Json;
using Api.Web.FinanceIA.Clases.Middleware;
using Api.Web.FinanceIA.Servicios.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The per-IP budget of the anonymous, key-guarded POST /api/bff/setup/first-admin.
/// <see cref="RateLimitService"/> leaves an unrecognised path completely unlimited, so without its
/// own branch the endpoint would ship with no limit at all on key guessing.
/// </summary>
public sealed class RateLimitServiceFirstAdminSetupTests
{
    private const string Path = "/api/bff/setup/first-admin";

    [Fact]
    public async Task WithoutTheConfigKey_DefaultsToFivePerMinutePerIp()
    {
        var sut = BuildSut();

        for (var i = 0; i < 5; i++)
        {
            Assert.True((await sut.CheckRateLimitAsync(BuildContext())).IsAllowed);
        }

        RateLimitResult refused = await sut.CheckRateLimitAsync(BuildContext());
        Assert.False(refused.IsAllowed);
        Assert.Equal("rate_limit_exceeded_ip", refused.ErrorCode);
    }

    [Fact]
    public async Task ConfiguredBudget_IsHonoured_AndIsPerIp()
    {
        var sut = BuildSut(("RateLimiting:PerIP:FirstAdminSetup", "2"));

        Assert.True((await sut.CheckRateLimitAsync(BuildContext())).IsAllowed);
        Assert.True((await sut.CheckRateLimitAsync(BuildContext())).IsAllowed);
        Assert.False((await sut.CheckRateLimitAsync(BuildContext())).IsAllowed);

        Assert.True((await sut.CheckRateLimitAsync(BuildContext(ip: "203.0.113.99"))).IsAllowed);
    }

    [Fact]
    public async Task Middleware_Answers429ProblemJson_WithRetryAfter()
    {
        var middleware = new RateLimitingMiddleware(
            _ => Task.CompletedTask,
            BuildSut(("RateLimiting:PerIP:FirstAdminSetup", "1")));

        await middleware.InvokeAsync(BuildContext());

        var context = BuildContext();
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        Assert.False(string.IsNullOrEmpty(context.Response.Headers.RetryAfter.ToString()));

        context.Response.Body.Position = 0;
        using JsonDocument body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("rate_limit_exceeded_ip", body.RootElement.GetProperty("errorCode").GetString());
    }

    private static RateLimitService BuildSut(params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();

        return new RateLimitService(
            new MemoryCache(new MemoryCacheOptions()),
            config,
            NullLogger<RateLimitService>.Instance);
    }

    private static DefaultHttpContext BuildContext(string ip = "203.0.113.7")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = Path;
        context.Request.Method = HttpMethods.Post;
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        return context;
    }
}
