// Project : Api.Web.FinanceIA
// Created : 2026-10-25

using Api.Web.FinanceIA.Models.Settings;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Reads and writes a tenant's SGR Fondo de Riesgo settings (TenantSettings keys <c>sgr.*</c>). Registered
/// Scoped. Pass <c>tenantId</c> as a parameter — never inject the tenant accessor.
/// </summary>
public interface ISgrSettingsService
{
    /// <summary>
    /// The settings in force, in one read. A stored value that does not parse or is not positive reads as
    /// not configured (and is logged by key). PROPAGATES a failed read.
    /// </summary>
    Task<SgrFundSettings> GetAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Validates and stores <paramref name="request"/> — all four keys in one SaveChanges — audits the change
    /// with the values before and after, and returns what is now stored.
    ///
    /// <para>
    /// THROWS <see cref="ArgumentException"/> carrying the Spanish validation message when the request is
    /// invalid, with <paramref name="nowUtc"/> deciding "today" on the Argentine calendar. A failed write
    /// propagates: the caller must not report a change that did not happen.
    /// </para>
    /// </summary>
    Task<SgrFundSettings> UpdateAsync(
        Guid userId,
        string tenantId,
        UpdateSgrSettingsRequest request,
        DateTime nowUtc,
        string? ipAddress,
        string? requestId,
        CancellationToken ct);
}
