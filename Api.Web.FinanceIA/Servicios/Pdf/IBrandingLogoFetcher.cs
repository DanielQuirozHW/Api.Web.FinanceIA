// Project : Api.Web.FinanceIA
// Created : 2026-11-08

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// Fetches and validates a tenant's PDF logo through the SSRF-guarded "BrandingLogoClient", caching the outcome
/// per (tenant, URL): a logo for 6 hours, a refusal for 15 minutes.
/// </summary>
public interface IBrandingLogoFetcher
{
    /// <summary>
    /// The validated logo at <paramref name="url"/>, or null when it was refused for any reason (logged once per
    /// fetch as a Warning with the tenant, the setting key, a reason code and the URL's host). Throws only
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled; that outcome is not cached.
    /// </summary>
    Task<PdfBrandLogo?> GetAsync(string tenantId, Uri url, CancellationToken ct);
}
