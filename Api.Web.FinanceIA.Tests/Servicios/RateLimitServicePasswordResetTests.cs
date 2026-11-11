// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-19

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
/// The per-IP budget of the anonymous POST /api/bff/auth/password-reset. <see cref="RateLimitService"/>
/// leaves an unrecognised path completely unlimited, so the endpoint needs its own branch; and
/// <c>GetValue&lt;int&gt;</c> reads a missing key as 0, so the budget needs an in-code default or a
/// configuration without the key would refuse every request. The last test runs the real middleware
/// to pin the 429 itself.
/// </summary>
public sealed class RateLimitServicePasswordResetTests
{
    private const string Path = "/api/bff/auth/password-reset";

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
        var sut = BuildSut(("RateLimiting:PerIP:PasswordReset", "2"));

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
            BuildSut(("RateLimiting:PerIP:PasswordReset", "1")));

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
