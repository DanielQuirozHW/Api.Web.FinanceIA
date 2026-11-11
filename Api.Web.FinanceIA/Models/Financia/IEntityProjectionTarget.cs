// Project : Api.Web.FinanceIA
// Created : 2026-08-25
// Modified: 2026-08-25

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The members <c>EntitiesController.EnrichProjectionAsync</c> touches when it fills in the
/// fields that need cross-row lookups. Implemented by both entity response shapes — the full
/// <see cref="EntityResponse"/> the detail and mutation endpoints return, and the narrow
/// <see cref="EntityListItemResponse"/> the list returns — so the three set-based queries and the
/// loop that applies them exist once instead of twice. A projection field added later cannot then
/// drift between the list and the detail path.
///
/// IMPORTANT: implement these IMPLICITLY on every DTO that carries them, never with explicit
/// interface implementation. All five are already public serialised properties on both DTOs, so an
/// implicit implementation adds nothing to the wire. An EXPLICIT one would hide the member from
/// System.Text.Json and silently drop <c>hasCreditLine</c> / <c>latestAnalysisStatus</c> /
/// <c>createdByName</c> from the JSON while the code still compiles and the enrichment still runs.
/// </summary>
internal interface IEntityProjectionTarget
{
    /// <summary>Key the credit-line and latest-analysis lookups run on.</summary>
    Guid EntityId { get; }

    /// <summary>Key the author-name lookup runs on. Null when the author is unknown.</summary>
    Guid? CreatedBy { get; }

    bool HasCreditLine { get; set; }
    string? LatestAnalysisStatus { get; set; }
    string? CreatedByName { get; set; }

    // The latest scored result, as one pair from one row (IAnalysisRepository.GetLatestScoreByEntityIdsAsync).
    decimal? LatestScore { get; set; }
    string? LatestRiskLevel { get; set; }
    Guid? LatestScoreAnalysisId { get; set; }
    DateTime? LatestScoredAt { get; set; }
}
