// Project : Api.Web.FinanceIA
// Created : 2026-06-20

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Read-only repository for <see cref="AuditLog"/> records.
/// The underlying table is append-only; no write operations are exposed here.
/// </summary>
public interface IAuditLogRepository
{
    /// <summary>
    /// Returns a paginated, filtered page of audit log entries for the given tenant.
    /// </summary>
    /// <param name="tenantId">The tenant whose records to query.</param>
    /// <param name="filter">Pagination and filter parameters.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <returns>The page of matching items and the total count before pagination.</returns>
    Task<(IReadOnlyList<AuditLog> Items, int Total)> GetPagedAsync(
        string tenantId, AuditLogFilter filter, CancellationToken ct);

    /// <summary>
    /// Returns all distinct action names recorded for the given tenant, sorted ascending.
    /// </summary>
    Task<IReadOnlyList<string>> GetDistinctActionsAsync(string tenantId, CancellationToken ct);
}
