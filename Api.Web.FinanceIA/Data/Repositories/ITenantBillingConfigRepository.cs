// Project : Api.Web.FinanceIA
// Created : 2026-08-31

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Access to per-tenant billing configuration ([dbo].[TenantBillingConfig]). Tenant-isolated.
/// Reads resolve the effective plan; the write is the SuperAdmin path behind
/// PUT /api/billing/config. Rows may also still be seeded by the DBA via SQL.
/// </summary>
public interface ITenantBillingConfigRepository
{
    /// <summary>
    /// Returns the tenant's ACTIVE billing configuration row, or null when it has none — either
    /// because no row exists or because the row is inactive. Callers apply the in-code default
    /// (<see cref="BillingPlans.Minimum"/>); this method never throws for an absent row.
    /// </summary>
    Task<TenantBillingConfig?> GetActiveByTenantAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Inserts or updates the tenant's single configuration row and returns the persisted entity.
    ///
    /// <para>
    /// The lookup deliberately IGNORES <see cref="TenantBillingConfig.IsActive"/>, unlike
    /// <see cref="GetActiveByTenantAsync"/>: an inactive row is still the tenant's one row under
    /// UQ_TenantBillingConfig_Tenant, so matching only active rows would try to INSERT a second
    /// one and violate the constraint.
    /// </para>
    ///
    /// <para>
    /// Unlike the read path, failures propagate — a billing write that silently did nothing would
    /// leave the operator believing a plan was changed when it was not.
    /// </para>
    /// </summary>
    Task<TenantBillingConfig> UpsertAsync(TenantBillingConfig config, CancellationToken ct);
}
