// Project : Api.Web.FinanceIA
// Created : 2026-11-08

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// Resolves the client identity one PDF export prints, from the tenant's <c>branding.*</c> settings. Pass
/// <c>tenantId</c> as a parameter — never inject the tenant accessor.
/// </summary>
public interface ITenantPdfBrandingProvider
{
    /// <summary>
    /// The tenant's branding for an export in <paramref name="theme"/>. Never throws into the export: any problem
    /// logs a Warning and resolves to <see cref="TenantPdfBranding.None"/> (or drops only the faulty part). Only
    /// the caller's <see cref="OperationCanceledException"/> propagates.
    /// </summary>
    Task<TenantPdfBranding> ResolveAsync(string tenantId, PdfTheme theme, CancellationToken ct);
}
