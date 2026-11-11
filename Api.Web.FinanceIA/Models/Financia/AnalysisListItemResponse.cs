// Project : Api.Web.FinanceIA
// Created : 2026-08-25
// Modified: 2026-10-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One row of GET /api/analyses — ONLY the fields the Portal's analyses list renders.
///
/// Deliberately NOT <see cref="AnalysisResultResponse"/>. The list used to reuse the detail DTO,
/// so every row shipped <c>balanceJson</c>, <c>ratiosJson</c>, <c>riskAlerts</c>, <c>anomalies</c>,
/// <c>executiveSummary</c> and <c>scoringNarrative</c> raw, plus five deserialised enrichment
/// snapshots (BCRA / LUFE / fiscal / CASFOG / NOSIS) and the whole <c>scoreBreakdown</c> object —
/// none of which the list view reads. That measured 213 KB for a single pageSize=200 page.
///
/// Every property here keeps the exact name and type it had on
/// <see cref="AnalysisResultResponse"/>, so the emitted camelCase key and JSON type are unchanged
/// for the fields that survive. Anything the list needs but this type lacks belongs on the DETAIL
/// endpoint (GET /api/analyses/{id}), which still returns the full DTO — do not widen this one
/// back towards it.
/// </summary>
public sealed class AnalysisListItemResponse
{
    public Guid ResultId { get; set; }
    public Guid AnalysisId { get; set; }

    /// <summary>
    /// One of <see cref="AnalysisKinds.Known"/> (DDL 072). A persona humana analysis carries no score and
    /// no level: <see cref="AiSuggestedScore"/> and <see cref="AiSuggestedLevel"/> are null by design.
    /// </summary>
    public string AnalysisKind { get; set; } = AnalysisKinds.Balance;

    /// <summary>Identifier of the entity being analysed. Sourced from the Analyses table.</summary>
    public Guid EntityId { get; set; }

    /// <summary>Legal business name of the entity being analysed. Sourced from the Entities table.</summary>
    public string? EntityName { get; set; }

    /// <summary>CUIT of the entity being analysed. Sourced from the Entities table.</summary>
    public string? EntityCuit { get; set; }

    /// <summary>Fiscal year of the analysed balance sheet. Sourced from the Analyses table.</summary>
    public int? FiscalYear { get; set; }

    /// <summary>Credit amount requested for this analysis. Sourced from the Analyses table.</summary>
    public decimal? RequestedAmount { get; set; }

    /// <summary>Workflow status (e.g. Queued, Processing, PendingReview, Reviewed, Approved, Rejected). Sourced from the Analyses table.</summary>
    public string? Status { get; set; }

    public DateTime GeneratedAt { get; set; }

    /// <summary>
    /// Composite risk score (0–100) from the deterministic scoring engine.
    /// Null for analyses created before the scoring engine was introduced.
    /// </summary>
    public decimal? AiSuggestedScore { get; set; }

    /// <summary>
    /// ALWAYS NULL. Superseded by <see cref="AiSuggestedScore"/> — the detail mapper has hard-set
    /// this to null since the scoring engine replaced Claude's own confidence figure. Kept on the
    /// list DTO purely so the key does not vanish from a payload the Portal already parses;
    /// nulls are serialised (DefaultIgnoreCondition is not configured), so it is emitted as
    /// <c>"confidenceScore": null</c> exactly as before. Read <see cref="AiSuggestedScore"/> instead.
    /// </summary>
    public decimal? ConfidenceScore { get; set; }

    /// <summary>Risk level label: Low / Medium / High / Critical. Null for pre-scoring analyses.</summary>
    public string? AiSuggestedLevel { get; set; }

    /// <summary>Final risk score assigned after review. Sourced from the Analyses table.</summary>
    public decimal? FinalRiskScore { get; set; }

    /// <summary>Final risk level assigned after review (Low | Medium | High | Critical). Sourced from the Analyses table.</summary>
    public string? FinalRiskLevel { get; set; }

    /// <summary>UserId of the analyst who requested the analysis.</summary>
    public string? RequestedBy { get; set; }

    /// <summary>Display name of the analyst who requested the analysis.</summary>
    public string? RequestedByName { get; set; }

    /// <summary>UserId of the reviewer, if reviewed.</summary>
    public string? ReviewedBy { get; set; }

    /// <summary>Display name of the reviewer, if reviewed.</summary>
    public string? ReviewedByName { get; set; }

    /// <summary>
    /// Amount the final decision actually committed (DDL 058) — on a confirmation, the confirming
    /// RiskManager's figure. Sourced from the Analyses table. Null unless the analysis is Approved,
    /// and null on an Approved analysis decided before DDL 058. The Portal reads it to offer only the
    /// analyses whose approved amount covers a cupo or a raise; the API enforces the same bound.
    /// </summary>
    public decimal? DecisionAmount { get; set; }

    /// <summary>ISO 4217 currency of <see cref="DecisionAmount"/>. Null exactly when it is.</summary>
    public string? DecisionCurrency { get; set; }

    // ── Flattened from scoreBreakdown ─────────────────────────────────────────
    // The list view reads four values out of the scoreBreakdown blob and nothing else, so the
    // blob is parsed once per row and these four are lifted onto the row instead of shipping the
    // whole object. Sourced from the ScoreBreakdownJson keys "sectorProfile", "criticalRatios",
    // "appliedFloor" and "bcraTrendDelta" (written in AnalysisPipeline.RunAsync). The full
    // object is still available on GET /api/analyses/{id}.

    /// <summary>Sector profile the scoring engine matched. Null when the analysis predates it.</summary>
    public string? SectorProfile { get; set; }

    /// <summary>
    /// How many ratios landed in the critical band. DERIVED — the blob stores the ratio KEYS under
    /// <c>criticalRatios</c>, not a count, so this is that array's length. Zero when the analysis
    /// has no breakdown, when the key is absent, or when the blob does not parse; the detail
    /// endpoint carries the names.
    /// </summary>
    public int CriticalRatiosCount { get; set; }

    /// <summary>
    /// Score floor computed from the critical ratios. Null when the analysis has no breakdown.
    /// <para>
    /// Non-zero does NOT mean the score was floored: the engine reports the computed floor even when
    /// the composite already exceeded it. Compare against <see cref="ScoreBeforeFloor"/> — the floor
    /// bound only when <c>ScoreBeforeFloor &lt; AppliedFloor</c>.
    /// </para>
    /// </summary>
    public decimal? AppliedFloor { get; set; }

    /// <summary>
    /// The composite score before the critical-ratio floor was considered. Null when the analysis has
    /// no breakdown, or when it predates the field. Carried purely so <see cref="AppliedFloor"/> can
    /// be read correctly — see its remarks.
    /// </summary>
    public decimal? ScoreBeforeFloor { get; set; }

    /// <summary>BCRA situación trend delta (positive = worsening). Null when the analysis has no breakdown.</summary>
    public int? BcraTrendDelta { get; set; }

    /// <summary>
    /// "Confiabilidad del score" (score credibility 1b-2b): the same verdict GET /api/analyses/{id} carries
    /// for this row, computed by the same adapter from the same columns. Null on a persona humana row.
    /// </summary>
    public ScoreReliabilityResponse? ScoreReliability { get; set; }
}
