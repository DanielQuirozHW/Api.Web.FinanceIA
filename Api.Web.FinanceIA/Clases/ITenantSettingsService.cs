// Project : Api.Web.FinanceIA
// Created : 2026-08-03

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Reads per-tenant product-behaviour settings, applying in-code defaults when a
/// tenant has no stored row. Registered Scoped and caches reads for the lifetime of
/// the request. Pass <c>tenantId</c> as a parameter — never inject the tenant accessor.
/// </summary>
public interface ITenantSettingsService
{
    /// <summary>
    /// Returns the string value for a setting, or <paramref name="defaultValue"/> when
    /// the tenant has no row for that key.
    /// </summary>
    Task<string> GetStringAsync(string tenantId, string key, string defaultValue, CancellationToken ct);

    /// <summary>
    /// Returns the credit-line display mode (<c>"sgr"</c> or <c>"simple"</c>) for the tenant.
    /// Unknown, empty or malformed stored values resolve to the default (<c>"sgr"</c>) so a
    /// bad row can never break the bootstrap path.
    /// </summary>
    Task<string> GetCreditLineModeAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns the tenant's company display name (<c>branding.company_name</c>), trimmed, or null
    /// when the tenant has no row, a blank one, or the read fails. There is no in-code default:
    /// null means "not configured" and the caller decides what to show instead.
    /// </summary>
    Task<string?> GetTenantDisplayNameAsync(string tenantId, CancellationToken ct);
}
