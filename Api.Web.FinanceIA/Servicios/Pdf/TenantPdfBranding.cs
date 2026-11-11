// Project : Api.Web.FinanceIA
// Created : 2026-11-08

using iText.Kernel.Colors;

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// The client identity one export prints, resolved by <see cref="ITenantPdfBrandingProvider"/>. Every part is
/// optional and null on any problem. The header switches to the client ONLY when <see cref="Logo"/> is present:
/// a company name or an accent without a usable logo leaves the export pure Financia, byte-identical to an export
/// with no branding at all (the provider already nulls both in that case).
/// </summary>
/// <param name="Logo">The validated logo; null means the Financia header.</param>
/// <param name="CompanyName">branding.company_name, printed beside the logo; null when unset.</param>
/// <param name="Accent">branding.accent_color when it clears 4.5:1 on the export theme's paper and surface.</param>
public sealed record TenantPdfBranding(PdfBrandLogo? Logo, string? CompanyName, DeviceRgb? Accent)
{
    /// <summary>No client branding: the Financia header and palette.</summary>
    public static TenantPdfBranding None { get; } = new(null, null, null);

    /// <summary>True when the header carries the client's logo — the only switch.</summary>
    public bool IsCoBranded => Logo is not null;
}
