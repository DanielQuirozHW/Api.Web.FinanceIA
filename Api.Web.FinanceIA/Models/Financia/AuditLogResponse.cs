// Project : Api.Web.FinanceIA
// Created : 2026-06-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Query parameters for filtering the audit log list.
/// </summary>
public sealed record AuditLogFilter
{
    /// <summary>1-based page number. Defaults to 1.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Maximum items per page (1–100). Defaults to 20.</summary>
    public int PageSize { get; init; } = 20;

    /// <summary>Filter by a specific user identifier.</summary>
    public Guid? UserId { get; init; }

    /// <summary>Filter by action name (exact, case-insensitive contains).</summary>
    public string? Action { get; init; }

    /// <summary>Filter by severity level.</summary>
    public string? Severity { get; init; }

    /// <summary>Inclusive lower bound on Timestamp (UTC).</summary>
    public DateTime? DateFrom { get; init; }

    /// <summary>Inclusive upper bound on Timestamp (UTC).</summary>
    public DateTime? DateTo { get; init; }

    /// <summary>
    /// Free-text search across Action, Source, EntityType, EntityId.
    /// Trimmed and capped at 200 characters by the controller before passing here.
    /// </summary>
    public string? Search { get; init; }
}

/// <summary>
/// A single row in the audit log list response.
/// OldValueJson, NewValueJson, and TenantId are intentionally excluded.
/// </summary>
public sealed record AuditLogItem
{
    public long AuditLogId { get; init; }
    public string Action { get; init; } = string.Empty;
    public string? EntityType { get; init; }
    public string? EntityId { get; init; }

    /// <summary>Resolved display name for the affected entity; null if unresolvable.</summary>
    public string? EntityName { get; init; }

    /// <summary>
    /// Human-readable label for the entity type (Spanish). Null when no entity type applies.
    /// </summary>
    public string? EntityTypeLabel { get; init; }

    /// <summary>Resolved display name of the user who performed the action.</summary>
    public string? UserDisplayName { get; init; }

    /// <summary>Resolved email of the user who performed the action.</summary>
    public string? UserEmail { get; init; }

    public string? IpAddress { get; init; }
    public string? RequestId { get; init; }
    public string? Source { get; init; }
    public string Severity { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
}

/// <summary>
/// Paginated list of audit log entries.
/// </summary>
public sealed class AuditLogListResponse
{
    public IReadOnlyList<AuditLogItem> Items { get; set; } = [];
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Total { get; set; }
}

/// <summary>
/// Distinct filter values available for the current tenant's audit log,
/// used to populate frontend filter dropdowns.
/// </summary>
public sealed record AuditLogFiltersResponse
{
    /// <summary>Distinct action names recorded for this tenant, sorted ascending.</summary>
    public IReadOnlyList<string> Actions { get; init; } = [];

    /// <summary>All recognised severity levels in ascending severity order.</summary>
    public IReadOnlyList<string> Severities { get; init; } = [];
}
