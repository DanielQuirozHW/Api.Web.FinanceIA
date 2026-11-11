// Project : Api.Web.FinanceIA
// Created : 2026-11-08

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using iText.Kernel.Colors;

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// Default <see cref="ITenantPdfBrandingProvider"/>. Scoped, like the settings repository it reads through.
/// </summary>
/// <remarks>
/// <para>The three keys are read in ONE query with the same fail-open reader the emails use
/// (<see cref="TenantEmailSettings.ReadAsync"/>), and the logo URL goes through the same https check
/// (<see cref="TenantEmailSettings.ResolveLogoUrl"/>).</para>
/// <para>The logo is the only switch. Without a usable one the result is <see cref="TenantPdfBranding.None"/>
/// whatever <c>branding.company_name</c> and <c>branding.accent_color</c> say, so a tenant that already has a
/// company name set (for the PDF Author and /auth/me) exports exactly what it exported before.</para>
/// </remarks>
public sealed class TenantPdfBrandingProvider : ITenantPdfBrandingProvider
{
    /// <summary>WCAG AA for body text: what every text-bearing palette token already clears.</summary>
    internal const double MinimumAccentContrast = 4.5;

    private static readonly string[] Keys =
    [
        TenantSettingKeys.BrandingLogoUrl,
        TenantSettingKeys.BrandingCompanyName,
        TenantSettingKeys.BrandingAccentColor
    ];

    private readonly ITenantSettingsRepository           _repository;
    private readonly IBrandingLogoFetcher                _logoFetcher;
    private readonly ILogger<TenantPdfBrandingProvider>  _logger;

    /// <summary>Initializes a new instance of <see cref="TenantPdfBrandingProvider"/>.</summary>
    public TenantPdfBrandingProvider(
        ITenantSettingsRepository repository,
        IBrandingLogoFetcher logoFetcher,
        ILogger<TenantPdfBrandingProvider> logger)
    {
        _repository  = repository;
        _logoFetcher = logoFetcher;
        _logger      = logger;
    }

    /// <inheritdoc />
    public async Task<TenantPdfBranding> ResolveAsync(string tenantId, PdfTheme theme, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        try
        {
            IReadOnlyDictionary<string, string> settings = await TenantEmailSettings
                .ReadAsync(_repository, tenantId, Keys, "PDF branding", _logger, ct)
                .ConfigureAwait(false);

            string? rawAccent = settings.GetValueOrDefault(TenantSettingKeys.BrandingAccentColor);

            string? logoUrl = TenantEmailSettings.ResolveLogoUrl(settings, tenantId, _logger);
            PdfBrandLogo? logo = logoUrl is null
                ? null
                : await _logoFetcher.GetAsync(tenantId, new Uri(logoUrl, UriKind.Absolute), ct).ConfigureAwait(false);

            if (logo is null)
            {
                if (rawAccent is not null)
                {
                    _logger.LogInformation(
                        "TenantSettings {SettingKey} for tenant {TenantId} not applied to the PDF: it only applies together with a usable {LogoKey}",
                        TenantSettingKeys.BrandingAccentColor, tenantId, TenantSettingKeys.BrandingLogoUrl);
                }

                return TenantPdfBranding.None;
            }

            return new TenantPdfBranding(
                logo,
                settings.GetValueOrDefault(TenantSettingKeys.BrandingCompanyName),
                ResolveAccent(rawAccent, tenantId, theme));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "PDF branding for tenant {TenantId} unavailable: {ExceptionType}; the PDF keeps the Financia header",
                tenantId, ex.GetType().Name);
            return TenantPdfBranding.None;
        }
    }

    // A strict #RRGGBB that reaches 4.5:1 against BOTH the theme's paper and its surface (the two backgrounds
    // accent text is printed on). Anything else is ignored with a Warning and the Financia accent stays.
    private DeviceRgb? ResolveAccent(string? raw, string tenantId, PdfTheme theme)
    {
        if (raw is null)
            return null;

        if (!PdfColorMath.TryParseHex(raw, out DeviceRgb accent))
        {
            _logger.LogWarning(
                "Ignoring TenantSettings {SettingKey} for tenant {TenantId}: not a #RRGGBB colour",
                TenantSettingKeys.BrandingAccentColor, tenantId);
            return null;
        }

        PdfPalette palette = PdfPalette.For(theme);
        double onPaper   = PdfColorMath.Contrast(accent, palette.Paper);
        double onSurface = PdfColorMath.Contrast(accent, palette.Surface);
        double worst     = Math.Min(onPaper, onSurface);

        if (worst < MinimumAccentContrast)
        {
            _logger.LogWarning(
                "Ignoring TenantSettings {SettingKey} {Accent} for tenant {TenantId} on the {Theme} theme: contrast {Contrast:0.00}:1 is below {Minimum}:1",
                TenantSettingKeys.BrandingAccentColor, PdfColorMath.ToHex(accent), tenantId,
                theme == PdfTheme.Dark ? "dark" : "light", worst, MinimumAccentContrast);
            return null;
        }

        return accent;
    }
}
