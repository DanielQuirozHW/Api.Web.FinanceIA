// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Legajo;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Data access for a legajo's reconciled inventory and incomes (DDL 071): [dbo].[LegajoItems] and
/// [dbo].[LegajoIncomes]. Every method is tenant-scoped; there is no global query filter.
/// </summary>
public interface ILegajoReconciliationRepository
{
    /// <summary>The legajo's items (read-only).</summary>
    Task<IReadOnlyList<LegajoItem>> GetItemsAsync(Guid personLegajoId, string tenantId, CancellationToken ct);

    /// <summary>The legajo's incomes (read-only).</summary>
    Task<IReadOnlyList<LegajoIncome>> GetIncomesAsync(Guid personLegajoId, string tenantId, CancellationToken ct);

    /// <summary>One item of the legajo (read-only), or null when it is not in this legajo and tenant.</summary>
    Task<LegajoItem?> GetItemAsync(Guid itemId, Guid personLegajoId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Writes the analyst columns of <paramref name="item"/> (IsHousing, IsActivityAsset, OwnershipPct,
    /// AnalystNote, EditedFields, ReconciliationStatus, ConfirmedFingerprint, UpdatedAt, UpdatedBy) —
    /// ONLY while the stored row still has <paramref name="expectedUpdatedAt"/>. False when a rebuild or
    /// another edit changed (or removed) it in between.
    /// </summary>
    Task<bool> TryUpdateItemAsync(LegajoItem item, DateTime expectedUpdatedAt, CancellationToken ct);

    /// <summary>
    /// Merges <paramref name="build"/> into the legajo's stored rows (<see cref="LegajoReconciliation.Merge"/>)
    /// and saves the whole diff in ONE SaveChanges. Throws <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>
    /// when it lost a race (UX_LegajoItems_Key, UX_LegajoIncomes_Key or the UpdatedAt token); nothing is
    /// saved then and the change tracker is cleared, so the caller may simply retry.
    /// </summary>
    Task<LegajoReconciliationMergeResult> ApplyRebuildAsync(
        Guid personLegajoId, string tenantId, LegajoReconciliationBuild build, DateTime now, CancellationToken ct);
}
