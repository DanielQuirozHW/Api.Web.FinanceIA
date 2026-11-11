// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-02

using Api.Web.FinanceIA.Servicios.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Guards the per-IP budget on POST /api/errorlog/client.
///
/// <para>
/// The endpoint is anonymous by design — the browser must be able to report a crash even when the
/// session is what broke — so the rate limit is the only thing standing between it and abuse.
/// <see cref="RateLimitService"/> dispatches on request path and its final branch returns
/// "check nothing", which means an endpoint without its own branch is not merely loosely limited
/// but <em>completely unlimited</em>. That silent default is what these tests exist to catch.
/// </para>
/// <para>
/// The second thing pinned here is the in-code fallback: <c>GetValue&lt;int&gt;</c> yields 0 for a
/// missing key and the service tests <c>count &gt; limit</c>, so a configuration without the key
/// would reject every single request rather than allowing them.
/// </para>
/// </summary>
public sealed class RateLimitServiceClientErrorLogTests
{
    private const string ClientErrorLogPath = "/api/errorlog/client";

    private static RateLimitService BuildSut(params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (key, value) in settings)
        {
            values[key] = value;
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return new RateLimitService(
            new MemoryCache(new MemoryCacheOptions()),
            config,
            NullLogger<RateLimitService>.Instance);
    }

    private static DefaultHttpContext BuildContext(string path, string ip = "203.0.113.7")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        return context;
    }

    [Fact]
    public async Task ClientErrorLog_AllowsRequestsUpToTheConfiguredLimit()
    {
        var sut = BuildSut(("RateLimiting:PerIP:ClientErrorLog", "10"));

        for (var i = 1; i <= 10; i++)
        {
            var result = await sut.CheckRateLimitAsync(BuildContext(ClientErrorLogPath));
            Assert.True(result.IsAllowed, $"request {i} should have been allowed");
        }
    }

    [Fact]
    public async Task ClientErrorLog_DeniesTheRequestPastTheLimit()
    {
        var sut = BuildSut(("RateLimiting:PerIP:ClientErrorLog", "10"));

        for (var i = 1; i <= 10; i++)
        {
            await sut.CheckRateLimitAsync(BuildContext(ClientErrorLogPath));
        }

        var denied = await sut.CheckRateLimitAsync(BuildContext(ClientErrorLogPath));

        Assert.False(denied.IsAllowed);
        Assert.Equal("rate_limit_exceeded_ip", denied.ErrorCode);
        Assert.True(denied.RetryAfterSeconds > 0);
    }

    [Fact]
    public async Task ClientErrorLog_CountsPerIpSoOneNoisyClientDoesNotBlockAnother()
    {
        var sut = BuildSut(("RateLimiting:PerIP:ClientErrorLog", "1"));

        await sut.CheckRateLimitAsync(BuildContext(ClientErrorLogPath, "203.0.113.7"));
        var sameIp = await sut.CheckRateLimitAsync(BuildContext(ClientErrorLogPath, "203.0.113.7"));
        var otherIp = await sut.CheckRateLimitAsync(BuildContext(ClientErrorLogPath, "198.51.100.4"));

        Assert.False(sameIp.IsAllowed);
        Assert.True(otherIp.IsAllowed);
    }

    [Fact]
    public async Task ClientErrorLog_WithoutTheConfigKey_FallsBackToTenRatherThanBlockingEverything()
    {
        var sut = BuildSut(); // no RateLimiting section at all

        for (var i = 1; i <= 10; i++)
        {
            var allowed = await sut.CheckRateLimitAsync(BuildContext(ClientErrorLogPath));
            Assert.True(allowed.IsAllowed, $"request {i} should have been allowed by the in-code default");
        }

        var denied = await sut.CheckRateLimitAsync(BuildContext(ClientErrorLogPath));
        Assert.False(denied.IsAllowed);
    }

    [Fact]
    public async Task ClientErrorLog_IsLimitedUnderTheBffRouteAliasToo()
    {
        var sut = BuildSut(("RateLimiting:PerIP:ClientErrorLog", "1"));

        await sut.CheckRateLimitAsync(BuildContext("/api/bff/errorlog/client"));
        var denied = await sut.CheckRateLimitAsync(BuildContext("/api/bff/errorlog/client"));

        Assert.False(denied.IsAllowed);
        Assert.Equal("rate_limit_exceeded_ip", denied.ErrorCode);
    }

    [Fact]
    public async Task ClientErrorLog_DoesNotConsumeTheOnboardingBudget()
    {
        var sut = BuildSut(
            ("RateLimiting:PerIP:ClientErrorLog", "10"),
            ("RateLimiting:PerIP:OnboardingToken", "1"));

        // Exhaust the error-log budget…
        for (var i = 1; i <= 11; i++)
        {
            await sut.CheckRateLimitAsync(BuildContext(ClientErrorLogPath));
        }

        // …the onboarding endpoint, keyed separately, is untouched.
        var onboarding = await sut.CheckRateLimitAsync(
            BuildContext("/api/bff/auth/onboarding-token"));

        Assert.True(onboarding.IsAllowed);
    }

    [Fact]
    public async Task OnboardingToken_KeepsItsOwnConfiguredLimit()
    {
        var sut = BuildSut(
            ("RateLimiting:PerIP:ClientErrorLog", "10"),
            ("RateLimiting:PerIP:OnboardingToken", "5"));

        for (var i = 1; i <= 5; i++)
        {
            var allowed = await sut.CheckRateLimitAsync(
                BuildContext("/api/bff/auth/onboarding-token"));
            Assert.True(allowed.IsAllowed, $"request {i} should have been allowed");
        }

        var denied = await sut.CheckRateLimitAsync(
            BuildContext("/api/bff/auth/onboarding-token"));

        Assert.False(denied.IsAllowed);
    }
}
