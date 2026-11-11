// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-19

using System.Net;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Servicios.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The per-IP rate-limit bucket used to be keyed on the LEFT-most X-Forwarded-For entry, which the
/// client writes, so rotating that header bought a fresh budget on every request. The client IP is
/// now resolved once by the real <see cref="ForwardedHeadersMiddleware"/>, built here with the same
/// <see cref="ClientIpForwarding.Configure"/> Program.cs uses, and <see cref="RateLimitService"/>
/// reads only <c>Connection.RemoteIpAddress</c>.
/// <para>
/// Requests are shaped like production: browser → Vercel (overwrites X-Forwarded-For with the client
/// IP) → App Service front end (appends Vercel's egress as <c>ip:port</c>) → app, whose connection
/// peer is the front end. Trusting one hop resolved Vercel and put every user in one bucket.
/// </para>
/// </summary>
public sealed class ClientIpResolutionTests
{
    private const string FrontEnd       = "10.0.0.5";
    private const string Vercel         = "76.76.21.21";
    private const string ClientA        = "203.0.113.7";
    private const string ClientB        = "198.51.100.50";
    private const string OnboardingPath = "/api/bff/auth/onboarding-token";
    private const string ResetPath      = "/api/bff/auth/password-reset";

    // ── Resolution ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThroughVercel_DefaultHops_ResolvesTheClient()
    {
        DefaultHttpContext context = Context(OnboardingPath, FrontEnd, $"{ClientA}, {Vercel}:443");

        await ResolveClientIpAsync(context);

        Assert.Equal(IPAddress.Parse(ClientA), context.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task ThroughVercel_ClientSuppliedSpoofInFront_StillResolvesTheClient()
    {
        DefaultHttpContext context = Context(OnboardingPath, FrontEnd, $"1.1.1.1, 2.2.2.2, {ClientA}, {Vercel}:443");

        await ResolveClientIpAsync(context);

        Assert.Equal(IPAddress.Parse(ClientA), context.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task OneTrustedHop_ResolvesTheLastEntry()
    {
        DefaultHttpContext throughVercel = Context(OnboardingPath, FrontEnd, $"{ClientA}, {Vercel}:443");
        DefaultHttpContext directToAzure = Context(OnboardingPath, FrontEnd, $"6.6.6.6, {ClientA}:51234");

        await ResolveClientIpAsync(throughVercel, trustedHops: 1);
        await ResolveClientIpAsync(directToAzure, trustedHops: 1);

        // The reason 1 is wrong behind Vercel, and right for a direct-to-Azure deployment.
        Assert.Equal(IPAddress.Parse(Vercel), throughVercel.Connection.RemoteIpAddress);
        Assert.Equal(IPAddress.Parse(ClientA), directToAzure.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task WithoutTheHeader_KeepsTheConnectionAddress()
    {
        DefaultHttpContext context = Context(OnboardingPath, ClientA, xff: null);

        await ResolveClientIpAsync(context);

        Assert.Equal(IPAddress.Parse(ClientA), context.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task ResidualRisk_DirectHitBypassingVercel_CanStillInjectTheSecondHop()
    {
        // Pinned on purpose: the App Service URL is public, so a caller who skips Vercel writes the
        // second trusted hop themselves. The planned fix is a Portal-added shared-secret header the
        // BFF requires before trusting that hop (see ClientIpForwarding). When it lands, this test
        // is the one to change.
        DefaultHttpContext context = Context(OnboardingPath, FrontEnd, $"6.6.6.6, {ClientA}:51234");

        await ResolveClientIpAsync(context);

        Assert.Equal(IPAddress.Parse("6.6.6.6"), context.Connection.RemoteIpAddress);
    }

    // ── Rate-limit buckets ───────────────────────────────────────────────────────

    [Fact]
    public async Task RateLimitService_IgnoresXForwardedFor_AndKeysOnTheConnection()
    {
        var sut = BuildSut(("RateLimiting:PerIP:OnboardingToken", "1"));

        Assert.True((await sut.CheckRateLimitAsync(Context(OnboardingPath, ClientA, "198.51.100.1"))).IsAllowed);

        // A different spoofed header, same connection: same bucket, so the budget of 1 is spent.
        RateLimitResult second = await sut.CheckRateLimitAsync(Context(OnboardingPath, ClientA, "198.51.100.2"));
        Assert.False(second.IsAllowed);
        Assert.Equal("rate_limit_exceeded_ip", second.ErrorCode);
    }

    [Fact]
    public async Task SpoofedPrefixes_CannotChangeTheBucket()
    {
        var sut = BuildSut(("RateLimiting:PerIP:OnboardingToken", "1"));

        Assert.True((await CheckThroughPipelineAsync(sut, OnboardingPath, $"6.6.6.6, {ClientA}, {Vercel}:1111")).IsAllowed);

        RateLimitResult second = await CheckThroughPipelineAsync(sut, OnboardingPath, $"7.7.7.7, {ClientA}, {Vercel}:2222");
        Assert.False(second.IsAllowed);
        Assert.Equal("rate_limit_exceeded_ip", second.ErrorCode);
    }

    [Fact]
    public async Task PasswordReset_PerIpBucket_IsPerRealClient_NotSharedThroughVercel()
    {
        // #251's in-code default: 5 per minute per IP.
        var sut = BuildSut();

        for (var i = 0; i < 5; i++)
        {
            Assert.True((await CheckThroughPipelineAsync(sut, ResetPath, $"{ClientA}, {Vercel}:{4000 + i}")).IsAllowed);
        }

        RateLimitResult sixth = await CheckThroughPipelineAsync(sut, ResetPath, $"{ClientA}, {Vercel}:4999");
        Assert.False(sixth.IsAllowed);
        Assert.Equal("rate_limit_exceeded_ip", sixth.ErrorCode);

        // Another user behind the same Vercel egress IP is untouched by A's exhausted budget.
        Assert.True((await CheckThroughPipelineAsync(sut, ResetPath, $"{ClientB}, {Vercel}:5000")).IsAllowed);
    }

    // ── Configuration ────────────────────────────────────────────────────────────

    [Fact]
    public void ReadTrustedHops_DefaultsToTwo()
        => Assert.Equal(2, ClientIpForwarding.ReadTrustedHops(Config()));

    [Fact]
    public void ReadTrustedHops_HonoursTheConfiguredValue()
        => Assert.Equal(3, ClientIpForwarding.ReadTrustedHops(Config((ClientIpForwarding.TrustedHopsKey, "3"))));

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void ReadTrustedHops_BelowOne_Throws(string value)
        => Assert.Throws<InvalidOperationException>(
            () => ClientIpForwarding.ReadTrustedHops(Config((ClientIpForwarding.TrustedHopsKey, value))));

    [Fact]
    public void Configure_TrustsTheGivenHops_FromAnyPeer()
    {
        var options = new ForwardedHeadersOptions();

        ClientIpForwarding.Configure(options, trustedHops: 2);

        Assert.Equal(2, options.ForwardLimit);
        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
        Assert.Empty(options.KnownNetworks);
        Assert.Empty(options.KnownProxies);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static async Task<RateLimitResult> CheckThroughPipelineAsync(RateLimitService sut, string path, string xff)
    {
        DefaultHttpContext context = Context(path, FrontEnd, xff);
        await ResolveClientIpAsync(context);
        return await sut.CheckRateLimitAsync(context);
    }

    private static Task ResolveClientIpAsync(HttpContext context, int trustedHops = ClientIpForwarding.DefaultTrustedHops)
    {
        var options = new ForwardedHeadersOptions();
        ClientIpForwarding.Configure(options, trustedHops);

        var middleware = new ForwardedHeadersMiddleware(
            _ => Task.CompletedTask,
            NullLoggerFactory.Instance,
            Options.Create(options));

        return middleware.Invoke(context);
    }

    private static RateLimitService BuildSut(params (string Key, string Value)[] settings)
        => new(
            new MemoryCache(new MemoryCacheOptions()),
            Config(settings),
            NullLogger<RateLimitService>.Instance);

    private static IConfiguration Config(params (string Key, string Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();

    private static DefaultHttpContext Context(string path, string remoteIp, string? xff)
    {
        var context = new DefaultHttpContext();
        context.Request.Path   = path;
        context.Request.Method = HttpMethods.Post;
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        if (xff is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = xff;
        }

        return context;
    }
}
