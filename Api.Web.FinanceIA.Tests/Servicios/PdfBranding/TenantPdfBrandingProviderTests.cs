// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Pdf;
using iText.Kernel.Colors;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfBranding;

/// <summary>
/// What <see cref="TenantPdfBrandingProvider"/> resolves from the three <c>branding.*</c> keys: the logo is the
/// only switch, nothing ever throws into the export, and the accent is kept only when it is legible.
/// </summary>
public sealed class TenantPdfBrandingProviderTests
{
    private const string Tenant  = "tenant-brand";
    private const string LogoUrl = "https://cdn.example.com/logo.png";

    private static readonly PdfBrandLogo Logo =
        new(BrandingFixtures.SolidPng(40, 10), 40, 10, PdfLogoFormat.Png);

    private static (TenantPdfBrandingProvider Provider, FakeFetcher Fetcher, RecordingLogger<TenantPdfBrandingProvider> Logger) NewProvider(
        Dictionary<string, string>? settings, PdfBrandLogo? logo = null, bool repositoryThrows = false, bool fetcherThrows = false)
    {
        var fetcher = new FakeFetcher(logo, fetcherThrows);
        var logger  = new RecordingLogger<TenantPdfBrandingProvider>();
        return (new TenantPdfBrandingProvider(new FakeSettings(settings ?? [], repositoryThrows), fetcher, logger), fetcher, logger);
    }

    private static Dictionary<string, string> Settings(string? logo = LogoUrl, string? name = null, string? accent = null)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (logo is not null)   d[TenantSettingKeys.BrandingLogoUrl]     = logo;
        if (name is not null)   d[TenantSettingKeys.BrandingCompanyName] = name;
        if (accent is not null) d[TenantSettingKeys.BrandingAccentColor] = accent;
        return d;
    }

    [Fact]
    public async Task NoSettings_IsNone_AndFetchesNothing()
    {
        var (provider, fetcher, _) = NewProvider([]);

        TenantPdfBranding branding = await provider.ResolveAsync(Tenant, PdfTheme.Light, CancellationToken.None);

        Assert.Same(TenantPdfBranding.None, branding);
        Assert.Equal(0, fetcher.Calls);
    }

    [Fact]
    public async Task CompanyNameAlone_DoesNotSwitchTheHeader()
    {
        // Several tenants already have branding.company_name (PDF Author, /auth/me): they must not change.
        var (provider, _, _) = NewProvider(Settings(logo: null, name: "SGR Ejemplo"));

        TenantPdfBranding branding = await provider.ResolveAsync(Tenant, PdfTheme.Light, CancellationToken.None);

        Assert.Same(TenantPdfBranding.None, branding);
        Assert.False(branding.IsCoBranded);
    }

    [Fact]
    public async Task AccentWithoutALogo_IsNotApplied_AndSaysSo()
    {
        var (provider, _, logger) = NewProvider(Settings(logo: null, name: "SGR Ejemplo", accent: "#1B4F9C"));

        TenantPdfBranding branding = await provider.ResolveAsync(Tenant, PdfTheme.Light, CancellationToken.None);

        Assert.Same(TenantPdfBranding.None, branding);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information
                                             && Equals(e.Values["SettingKey"], TenantSettingKeys.BrandingAccentColor));
    }

    [Fact]
    public async Task UsableLogo_IsCoBranded_WithTheCompanyName()
    {
        var (provider, fetcher, _) = NewProvider(Settings(name: "  SGR Ejemplo "), Logo);

        TenantPdfBranding branding = await provider.ResolveAsync(Tenant, PdfTheme.Light, CancellationToken.None);

        Assert.True(branding.IsCoBranded);
        Assert.Same(Logo, branding.Logo);
        Assert.Equal("SGR Ejemplo", branding.CompanyName);   // trimmed by the shared reader
        Assert.Null(branding.Accent);
        Assert.Equal(new Uri(LogoUrl), fetcher.LastUrl);
    }

    [Fact]
    public async Task RefusedLogo_IsNone_EvenWithANameAndAnAccent()
    {
        var (provider, _, _) = NewProvider(Settings(name: "SGR Ejemplo", accent: "#1B4F9C"), logo: null);

        Assert.Same(TenantPdfBranding.None, await provider.ResolveAsync(Tenant, PdfTheme.Light, CancellationToken.None));
    }

    [Theory]
    [InlineData("http://cdn.example.com/logo.png")]
    [InlineData("cdn.example.com/logo.png")]
    [InlineData("/logo.png")]
    public async Task NonHttpsLogoUrl_IsIgnored_WithoutAFetch(string url)
    {
        var (provider, fetcher, logger) = NewProvider(Settings(logo: url), Logo);

        Assert.Same(TenantPdfBranding.None, await provider.ResolveAsync(Tenant, PdfTheme.Light, CancellationToken.None));
        Assert.Equal(0, fetcher.Calls);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task SettingsReadThatThrows_IsNone_NotAnException()
    {
        var (provider, _, logger) = NewProvider([], Logo, repositoryThrows: true);

        Assert.Same(TenantPdfBranding.None, await provider.ResolveAsync(Tenant, PdfTheme.Light, CancellationToken.None));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task FetcherThatThrows_IsNone_NotAnException()
    {
        var (provider, _, logger) = NewProvider(Settings(), fetcherThrows: true);

        Assert.Same(TenantPdfBranding.None, await provider.ResolveAsync(Tenant, PdfTheme.Light, CancellationToken.None));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        var (provider, _, _) = NewProvider(Settings(), Logo);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ResolveAsync(Tenant, PdfTheme.Light, cts.Token));
    }

    // ── Accent ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("#1B4F9C", PdfTheme.Light)]   // dark blue on white
    [InlineData("#1b4f9c", PdfTheme.Light)]
    [InlineData(" #7A1F2B ", PdfTheme.Light)]
    [InlineData("#FFD400", PdfTheme.Dark)]   // yellow on navy
    [InlineData("#8FB8FF", PdfTheme.Dark)]
    public async Task LegibleAccent_IsKept(string accent, PdfTheme theme)
    {
        var (provider, _, logger) = NewProvider(Settings(accent: accent), Logo);

        TenantPdfBranding branding = await provider.ResolveAsync(Tenant, theme, CancellationToken.None);

        Assert.NotNull(branding.Accent);
        Assert.True(PdfColorMath.TryParseHex(accent, out DeviceRgb expected));
        Assert.Equal(PdfColorMath.ToHex(expected), PdfColorMath.ToHex(branding.Accent));
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    // The guard against the shipped palettes: each theme's own accent, offered as a client accent, is legible on
    // that theme (PdfPaletteContrastTests demands 4.5:1 of every text token, Accent included).
    [Theory]
    [InlineData(PdfTheme.Light)]
    [InlineData(PdfTheme.Dark)]
    public async Task TheThemesOwnAccent_PassesTheGuard(PdfTheme theme)
    {
        string accent = PdfColorMath.ToHex(PdfPalette.For(theme).Accent);
        var (provider, _, logger) = NewProvider(Settings(accent: accent), Logo);

        TenantPdfBranding branding = await provider.ResolveAsync(Tenant, theme, CancellationToken.None);

        Assert.Equal(accent, PdfColorMath.ToHex(branding.Accent!));
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Theory]
    [InlineData("#FFD400", PdfTheme.Light)]   // yellow on white: ~1.4:1
    [InlineData("#1B4F9C", PdfTheme.Dark)]    // dark blue on navy
    [InlineData("#2A6FDB", PdfTheme.Dark)]    // the manual's light accent is too dark for navy
    [InlineData("#2A6FDB", PdfTheme.Light)]   // …and 4.45:1 on the light Surface, which is why the shipped one is 296EDB
    public async Task IllegibleAccent_IsIgnored_PerTheme(string accent, PdfTheme theme)
    {
        var (provider, _, logger) = NewProvider(Settings(accent: accent), Logo);

        TenantPdfBranding branding = await provider.ResolveAsync(Tenant, theme, CancellationToken.None);

        Assert.True(branding.IsCoBranded);   // the logo stays; only the accent is dropped
        Assert.Null(branding.Accent);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(TenantSettingKeys.BrandingAccentColor, warning.Values["SettingKey"]);
        Assert.Equal(Tenant, warning.Values["TenantId"]);
    }

    [Theory]
    [InlineData("1B4F9C")]
    [InlineData("#1B4F9")]
    [InlineData("#1B4F9C80")]
    [InlineData("#GGGGGG")]
    [InlineData("blue")]
    [InlineData("rgb(27,79,156)")]
    public async Task MalformedAccent_IsIgnored(string accent)
    {
        var (provider, _, logger) = NewProvider(Settings(accent: accent), Logo);

        TenantPdfBranding branding = await provider.ResolveAsync(Tenant, PdfTheme.Light, CancellationToken.None);

        Assert.True(branding.IsCoBranded);
        Assert.Null(branding.Accent);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class FakeFetcher(PdfBrandLogo? logo, bool throws) : IBrandingLogoFetcher
    {
        public int Calls { get; private set; }

        public Uri? LastUrl { get; private set; }

        public Task<PdfBrandLogo?> GetAsync(string tenantId, Uri url, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            LastUrl = url;
            return throws ? throw new InvalidOperationException("boom") : Task.FromResult(logo);
        }
    }

    private sealed class FakeSettings(Dictionary<string, string> values, bool throws) : ITenantSettingsRepository
    {
        public Task<string?> GetValueAsync(string tenantId, string settingKey, CancellationToken ct) =>
            Task.FromResult(values.GetValueOrDefault(settingKey));

        public Task<TenantSettingEntry?> GetEntryAsync(string tenantId, string settingKey, CancellationToken ct) =>
            Task.FromResult<TenantSettingEntry?>(null);

        public Task<IReadOnlyDictionary<string, string>> GetValuesAsync(
            string tenantId, IReadOnlyCollection<string> settingKeys, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (throws)
                throw new InvalidOperationException("db down");

            IReadOnlyDictionary<string, string> found = values
                .Where(kv => settingKeys.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            return Task.FromResult(found);
        }

        public Task UpsertAsync(string tenantId, string settingKey, string settingValue, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task SetValuesAsync(string tenantId, IReadOnlyDictionary<string, string?> values, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
