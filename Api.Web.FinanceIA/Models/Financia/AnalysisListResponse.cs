// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-08-25

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Paginated response for GET /api/analyses.
/// </summary>
public sealed class AnalysisListResponse
{
    /// <summary>
    /// The analysis results on the current page, as the lightweight list projection.
    /// Carries only what the list view renders — see <see cref="AnalysisListItemResponse"/> for
    /// why this is not the full <see cref="AnalysisResultResponse"/>.
    /// </summary>
    public IReadOnlyList<AnalysisListItemResponse> Items { get; set; } = [];

    /// <summary>Current page number (1-based).</summary>
    public int Page { get; set; }

    /// <summary>Maximum number of items per page.</summary>
    public int PageSize { get; set; }

    /// <summary>Total number of results matching the query (before pagination).</summary>
    public int Total { get; set; }
}
