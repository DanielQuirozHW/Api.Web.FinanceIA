// Project : Api.Web.FinanceIA
// Created : 2026-11-03

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>One document in Processing, as GET /api/analyses/runs lists it.</summary>
public sealed record AnalysisRunRunningItem
{
    public required Guid DocumentId { get; init; }

    public required Guid EntityId { get; init; }

    /// <summary>The entity's business name; null when it cannot be resolved.</summary>
    public string? EntityName { get; init; }

    /// <summary>The document's original file name.</summary>
    public required string FileName { get; init; }

    /// <summary>
    /// When the document was claimed for this run. Best available: known only for a run held by the
    /// instance that answers (nothing durable records it); null otherwise.
    /// </summary>
    public DateTime? StartedAt { get; init; }

    /// <summary>Display name of the analyst who triggered the run; null when it cannot be resolved.</summary>
    public string? RequestedBy { get; init; }

    /// <summary>
    /// The run's automatic retry, when its first attempt failed for a transient reason of ours:
    /// <c>scheduled</c> (waiting) or <c>running</c>, with the time it was due. Null when no automatic retry
    /// applies — and for a run held by another instance or orphaned by a restart (in memory only).
    /// </summary>
    public AnalysisRunAutoRetry? AutoRetry { get; init; }
}
