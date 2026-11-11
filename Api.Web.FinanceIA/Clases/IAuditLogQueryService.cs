// Project : Api.Web.FinanceIA
// Created : 2026-06-20

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Orchestrates audit log queries: retrieves paginated results from the repository
/// and enriches each entry with resolved user display names and entity names.
/// </summary>
public interface IAuditLogQueryService
{
    /// <summary>
    /// Returns a paginated, enriched list of audit log entries for the given tenant.
    /// </summary>
    Task<AuditLogListResponse> GetLogsAsync(
        string tenantId, AuditLogFilter filter, CancellationToken ct);

    /// <summary>
    /// Returns the distinct filter values (actions, severities) available for the tenant.
    /// </summary>
    Task<AuditLogFiltersResponse> GetFiltersAsync(string tenantId, CancellationToken ct);
}
