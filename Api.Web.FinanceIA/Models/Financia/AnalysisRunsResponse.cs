// Project : Api.Web.FinanceIA
// Created : 2026-11-03

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// 200 body of GET /api/analyses/runs: the tenant's balance analysis runs that are in flight, that
/// failed inside the window, and that finished in the last 24 hours. Each list is newest first and
/// capped at <c>AnalysisRunListService.MaxItemsPerList</c>.
/// </summary>
public sealed record AnalysisRunsResponse
{
    /// <summary>The failed list's window, in days (the <c>days</c> query parameter, 1..30).</summary>
    public required int WindowDays { get; init; }

    /// <summary>UTC time the lists were read; the windows are measured back from it.</summary>
    public required DateTime GeneratedAt { get; init; }

    /// <summary>Documents in Processing: queued or running, synchronous or background.</summary>
    public required IReadOnlyList<AnalysisRunRunningItem> Running { get; init; }

    /// <summary>Documents whose run failed within the window.</summary>
    public required IReadOnlyList<AnalysisRunFailedItem> Failed { get; init; }

    /// <summary>Documents whose run completed within the last 24 hours.</summary>
    public required IReadOnlyList<AnalysisRunCompletedItem> Completed { get; init; }
}
