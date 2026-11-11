// Project : Api.Web.FinanceIA
// Created : 2026-11-03

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>One document whose run completed recently, as GET /api/analyses/runs lists it.</summary>
public sealed record AnalysisRunCompletedItem
{
    public required Guid DocumentId { get; init; }

    public required Guid EntityId { get; init; }

    /// <summary>The entity's business name; null when it cannot be resolved.</summary>
    public string? EntityName { get; init; }

    /// <summary>The document's original file name.</summary>
    public required string FileName { get; init; }

    /// <summary>The result to open: GET /api/analyses/{resultId} (the Portal's /dashboard/analyses/{id}).</summary>
    public required Guid ResultId { get; init; }

    /// <summary>When the run completed (Documents.ProcessedAt).</summary>
    public required DateTime FinishedAt { get; init; }
}
