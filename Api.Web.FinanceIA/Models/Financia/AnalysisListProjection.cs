// Project : Api.Web.FinanceIA
// Created : 2026-08-25
// Modified: 2026-08-25

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The columns of <see cref="AnalysisResult"/> that GET /api/analyses actually consumes — and
/// nothing else. A REPOSITORY projection, not a wire contract: it is the target of the
/// <c>.Select(...)</c> in <c>AnalysisResultRepository.BuildListPage</c>, so EF emits a SELECT naming
/// exactly these columns. The response DTO clients see is <see cref="AnalysisListItemResponse"/>.
///
/// <para>
/// This is the other half of the fix #137 started. #137 stopped <em>shipping</em> the heavy columns;
/// the query still read them. <see cref="AnalysisResult"/> has 15 <c>nvarchar(max)</c> columns —
/// BalanceJson, RatiosJson, BcraDataJson, MacroDataJson, ExecutiveSummary, ScoringNarrative,
/// RiskAlerts, CatalogueAlerts, Anomalies, ValidationNotes, LufeDataJson, FiscalDataJson,
/// NosisDataJson, CasfogDataJson (and ScoreBreakdownJson) — and SQL Server read every one of them off
/// disk for every row of the tenant on every list request. That read was the 8–30s.
/// </para>
///
/// <para>
/// <see cref="ScoreBreakdownJson"/> is the deliberate exception: the four scoring signals the list
/// renders (sectorProfile, criticalRatiosCount, appliedFloor, bcraTrendDelta) have no columns of
/// their own, so that one blob must be read and parsed. It is small — four scalars and a short array
/// of ratio keys — and only ≤pageSize rows of it are ever fetched. Adding any OTHER *Json property
/// here silently reintroduces the whole defect; <c>ListPageQuery_SqlNeverNamesAHeavyBlobColumn</c>
/// and the shape-lock test exist to stop that.
/// </para>
/// </summary>
public sealed class AnalysisListProjection
{
    /// <summary>Primary key. Also the ORDER BY tie-break that makes paging stable.</summary>
    public Guid ResultId { get; set; }

    /// <summary>FK to Analyses — the key every enrichment lookup joins on.</summary>
    public Guid AnalysisId { get; set; }

    /// <summary>UTC. The sort key: the list is newest-first on this column.</summary>
    public DateTime GeneratedAt { get; set; }

    public decimal? AiSuggestedScore { get; set; }

    public string? AiSuggestedLevel { get; set; }

    /// <summary>
    /// The BCRA enrichment outcome (a <see cref="BcraEnrichmentOutcome"/> name). A short scalar, read only for
    /// the score-reliability verdict (score credibility 1b-2b).
    /// </summary>
    public string? EnrichmentStatus { get; set; }

    /// <summary>
    /// UTC; the latest re-score, null when never re-scored. With <see cref="GeneratedAt"/> it is the score's
    /// date, which the score-reliability verdict measures every age against.
    /// </summary>
    public DateTime? RescoredAt { get; set; }

    /// <summary>
    /// The ONLY nvarchar(max) column the list query reads. Parsed per page row by
    /// <c>AnalysisController.TryDeserializeListBreakdown</c> into four scalars; never echoed whole
    /// (GET /api/analyses/{resultId} still returns the full object).
    /// </summary>
    public string? ScoreBreakdownJson { get; set; }
}
