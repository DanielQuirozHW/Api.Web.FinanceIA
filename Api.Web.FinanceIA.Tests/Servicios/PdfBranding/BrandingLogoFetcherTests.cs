// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using System.Net;
using System.Net.Http;
using Api.Web.FinanceIA.Servicios.Pdf;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfBranding;

/// <summary>
/// Every way <see cref="BrandingLogoFetcher"/> refuses a tenant logo, and its cache. The "server" is an in-memory
/// handler (no network); the address checks the real client adds are covered by
/// <see cref="BrandingLogoAddressPolicyTests"/> and <see cref="BrandingLogoConnectPolicyTests"/>.
/// </summary>
public sealed class BrandingLogoFetcherTests
{
    private const string Tenant  = "tenant-brand";
    private static readonly Uri LogoUrl = new("https://cdn.example.com/brand/secret-path/logo.png");

    private static (BrandingLogoFetcher Fetcher, RecordingLogger<BrandingLogoFetcher> Logger) NewFetcher(
        HttpMessageHandler server, IMemoryCache? cache = null, TimeSpan? timeout = null)
    {
        var logger = new RecordingLogger<BrandingLogoFetcher>();
        var fetcher = new BrandingLogoFetcher(
            new FakeHttpClientFactory(server), cache ?? new MemoryCache(new MemoryCacheOptions()), logger)
        {
            FetchTimeout = timeout ?? TimeSpan.FromSeconds(5)
        };
        return (fetcher, logger);
    }

    private static string? RefusalReason(RecordingLogger<BrandingLogoFetcher> logger) =>
        logger.Entries.SingleOrDefault(e => e.Level == LogLevel.Warning)?.Values.GetValueOrDefault("Reason") as string;

    // ── Accepted ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Png_IsAccepted_WithItsPixelSize()
    {
        var server = FakeLogoServer.Serving(BrandingFixtures.SolidPng(300, 80));
        var (fetcher, logger) = NewFetcher(server);

        PdfBrandLogo? logo = await fetcher.GetAsync(Tenant, LogoUrl, CancellationToken.None);

        Assert.NotNull(logo);
        Assert.Equal(PdfLogoFormat.Png, logo.Format);
        Assert.Equal((300, 80), (logo.PixelWidth, logo.PixelHeight));
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Jpeg_IsAccepted_WhateverContentTypeItIsServedAs()
    {
        var server = FakeLogoServer.Serving(BrandingFixtures.Jpeg40x20, contentType: "text/plain");
        var (fetcher, _) = NewFetcher(server);

        PdfBrandLogo? logo = await fetcher.GetAsync(Tenant, LogoUrl, CancellationToken.None);

        Assert.NotNull(logo);
        Assert.Equal(PdfLogoFormat.Jpeg, logo.Format);
        Assert.Equal((40, 20), (logo.PixelWidth, logo.PixelHeight));
    }

    [Fact]
    public async Task ExactlyOneMegabyte_IsNotOversize()
    {
        byte[] png = BrandingFixtures.SolidPng(16, 16);
        byte[] padded = new byte[BrandingLogoFetcher.MaxBytes];
        png.CopyTo(padded, 0);   // trailing zeros after IEND: still a PNG to the probe and to iText

        var (fetcher, logger) = NewFetcher(FakeLogoServer.Serving(padded, sendLength: false));

        await fetcher.GetAsync(Tenant, LogoUrl, CancellationToken.None);

        Assert.NotEqual(BrandingLogoFailure.Oversize, RefusalReason(logger));
    }

    // ── Refused before any request ────────────────────────────────────────────

    [Theory]
    [InlineData("http://cdn.example.com/logo.png", "scheme")]
    [InlineData("ftp://cdn.example.com/logo.png", "scheme")]
    [InlineData("https://cdn.example.com:8443/logo.png", "port")]
    [InlineData("https://user:pass@cdn.example.com/logo.png", "userinfo")]
    public async Task UrlRules_RefuseWithoutAnyRequest(string url, string reason)
    {
        var server = FakeLogoServer.Serving(BrandingFixtures.SolidPng(10, 10));
        var (fetcher, logger) = NewFetcher(server);

        Assert.Null(await fetcher.GetAsync(Tenant, new Uri(url), CancellationToken.None));
        Assert.Equal(0, server.Calls);
        Assert.Equal(reason, RefusalReason(logger));
    }

    // ── Refused from the response ─────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task Redirect_IsRefused_AndNeverFollowed(HttpStatusCode status)
    {
        var server = FakeLogoServer.Status(status, location: "https://169.254.169.254/latest/meta-data/");
        var (fetcher, logger) = NewFetcher(server);

        Assert.Null(await fetcher.GetAsync(Tenant, LogoUrl, CancellationToken.None));
        Assert.Equal(1, server.Calls);
        Assert.Equal(BrandingLogoFailure.Redirect, RefusalReason(logger));
    }

    [Fact]
    public async Task NotFound_IsRefused()
    {
        var (fetcher, logger) = NewFetcher(FakeLogoServer.Status(HttpStatusCode.NotFound));

        Assert.Null(await fetcher.GetAsync(Tenant, LogoUrl, CancellationToken.None));
        Assert.Equal(BrandingLogoFailure.HttpStatus, RefusalReason(logger));
    }

    [Fact]
    public async Task DeclaredLengthOverOneMegabyte_IsRefused()
    {
        byte[] body = new byte[(2 * 1024 * 1024)];
        BrandingFixtures.SolidPng(10, 10).CopyTo(body, 0);
        var (fetcher, logger) = NewFetcher(FakeLogoServer.Serving(body));

        Assert.Null(await fetcher.GetAsync(Tenant, LogoUrl, CancellationToken.None));
        Assert.Equal(BrandingLogoFailure.Oversize, RefusalReason(logger));
    }

    [Fact]
    public async Task UndeclaredBodyOverOneMegabyte_IsAbandonedAsItIsRead()
    {
        byte[] body = new byte[BrandingLogoFetcher.MaxBytes + 1];
        BrandingFixtures.SolidPng(10, 10).CopyTo(body, 0);
        var (fetcher, logger) = NewFetcher(FakeLogoServer.Serving(body, sendLength: false));

        Assert.Null(await fetcher.GetAsync(Tenant, LogoUrl, CancellationToken.None));
        Assert.Equal(BrandingLogoFailure.Oversize, RefusalReason(logger));
    }

    public static TheoryData<string, byte[], string> WrongFormats => new()
    {
        { "gif",                BrandingFixtures.Gif, "image/gif" },
        { "webp",               BrandingFixtures.WebP, "image/webp" },
        { "svg",                BrandingFixtures.Svg, "image/svg+xml" },
        { "svg served as png",  BrandingFixtures.Svg, "image/png" },
        { "gif served as png",  BrandingFixtures.Gif, "image/png" },
        { "html",               System.Text.Encoding.UTF8.GetBytes("<html>login</html>"), "image/png" },
        { "empty",              [], "image/png" },
    };

    [Theory]
    [MemberData(nameof(WrongFormats))]
    public async Task AnythingButPngOrJpeg_IsRefused_ByItsBytes(string name, byte[] body, string contentType)
    {
        _ = name;
        var (fetcher, logger) = NewFetcher(FakeLogoServer.Serving(body, contentType));

        Assert.Null(await fetcher.GetAsync(Tenant, LogoUrl, CancellationToken.None));
        Assert.Equal(BrandingLogoFailure.Format, RefusalReason(logger));
    }

    [Theory]
    [InlineData(4001, 10)]
    [InlineData(10, 4001)]
    [InlineData(0, 10)]
    public async Task OutOfRangeDimensions_AreRefused_BeforeDecoding(int width, int height)
    {
        // Header only: the dimensions are refused from IHDR alone, nothing is decoded.
        var (fetcher, logger) = NewFetcher(FakeLogoServer.Serving(BrandingFixtures.PngHeaderOnly(width, height)));

        Assert.Null(await fetcher.GetAsync(Tenant, LogoUrl, CancellationToken.None));
        Assert.Equal(BrandingLogoFailure.Dimensions, RefusalReason(logger));
    }

    [Fact]
    public async Task PngWithNoImageData_IsRefused_AsUndecodable()
    {
        var (fetcher, logger) = NewFetcher(FakeLogoServer.Serving(BrandingFixtures.PngHeaderOnly(100, 40)));

        Assert.Null(await fetcher.GetAsync(Tenant, LogoUrl, CancellationToken.None));
        Assert.Equal(BrandingLogoFailure.Undecodable, RefusalReason(logger));
    }

    [Fact]
    public async Task SlowServer_IsRefused_AsTimeout()
    {
        var (fetcher, logger) = NewFetcher(FakeLogoServer.Hanging(), timeout: TimeSpan.FromMilliseconds(100));

        Assert.Null(await fetcher.GetAsync(Tenant, LogoUrl, CancellationToken.None));
        Assert.Equal(BrandingLogoFailure.Timeout, RefusalReason(logger));
    }

    [Fact]
    public async Task BlockedAddress_FromTheConnectPolicy_IsRefused_AsBlockedAddress()
    {
        // What SocketsHttpHandler surfaces when the ConnectCallback throws: an HttpRequestException around it.
        var server = new FakeLogoServer((_, _) => throw new HttpRequestException(
            "connect failed", new BrandingLogoBlockedAddressException("cdn.example.com")));
        var (fetcher, logger) = NewFetcher(server);

        Assert.Null(await fetcher.GetAsync(Tenant, LogoUrl, CancellationToken.None));
        Assert.Equal(BrandingLogoFailure.BlockedAddress, RefusalReason(logger));
    }

    [Fact]
    public async Task NetworkFailure_IsRefused_AsNetwork()
    {
        var server = new FakeLogoServer((_, _) => throw new HttpRequestException("tls failed"));
        var (fetcher, logger) = NewFetcher(server);

        Assert.Null(await fetcher.GetAsync(Tenant, LogoUrl, CancellationToken.None));
        Assert.Equal(BrandingLogoFailure.Network, RefusalReason(logger));
    }

    // ── Cancellation, cache and log hygiene ───────────────────────────────────

    [Fact]
    public async Task CallerCancellation_Propagates_AndIsNotCached()
    {
        var cache  = new MemoryCache(new MemoryCacheOptions());
        var (fetcher, _) = NewFetcher(FakeLogoServer.Hanging(), cache);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetcher.GetAsync(Tenant, LogoUrl, cts.Token));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task Success_IsCached_SoTheSecondExportMakesNoRequest()
    {
        var server = FakeLogoServer.Serving(BrandingFixtures.SolidPng(30, 30));
        var (fetcher, _) = NewFetcher(server);

        PdfBrandLogo? first  = await fetcher.GetAsync(Tenant, LogoUrl, CancellationToken.None);
        PdfBrandLogo? second = await fetcher.GetAsync(Tenant, LogoUrl, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(1, server.Calls);
    }

    [Fact]
    public async Task Refusal_IsCached_SoABrokenUrlCostsOneTimeoutNotOnePerExport()
    {
        var server = FakeLogoServer.Hanging();
        var (fetcher, logger) = NewFetcher(server, timeout: TimeSpan.FromMilliseconds(100));

        Assert.Null(await fetcher.GetAsync(Tenant, LogoUrl, CancellationToken.None));
        Assert.Null(await fetcher.GetAsync(Tenant, LogoUrl, CancellationToken.None));

        Assert.Equal(1, server.Calls);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);   // the second is a Debug line
    }

    [Fact]
    public async Task Cache_IsPerTenantAndUrl()
    {
        var server = FakeLogoServer.Serving(BrandingFixtures.SolidPng(30, 30));
        var (fetcher, _) = NewFetcher(server);

        await fetcher.GetAsync(Tenant, LogoUrl, CancellationToken.None);
        await fetcher.GetAsync("tenant-other", LogoUrl, CancellationToken.None);
        await fetcher.GetAsync(Tenant, new Uri("https://cdn.example.com/brand/logo-v2.png"), CancellationToken.None);

        Assert.Equal(3, server.Calls);
    }

    [Fact]
    public void CacheDurations_AreSixHoursAndFifteenMinutes()
    {
        Assert.Equal(TimeSpan.FromHours(6), BrandingLogoFetcher.SuccessCacheDuration);
        Assert.Equal(TimeSpan.FromMinutes(15), BrandingLogoFetcher.FailureCacheDuration);
    }

    [Fact]
    public async Task RefusalWarning_NamesTenantKeyReasonAndHost_NeverThePath()
    {
        var (fetcher, logger) = NewFetcher(FakeLogoServer.Status(HttpStatusCode.Forbidden));

        await fetcher.GetAsync(Tenant, LogoUrl, CancellationToken.None);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(Tenant, warning.Values["TenantId"]);
        Assert.Equal("branding.logo_url", warning.Values["SettingKey"]);
        Assert.Equal("cdn.example.com", warning.Values["LogoHost"]);
        Assert.DoesNotContain("secret-path", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("logo.png", warning.Message, StringComparison.Ordinal);
    }
}
