// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.ScoreReliability;

/// <summary>
/// The row scalars every surface can project: the analysis detail, the list and the dashboard all have
/// these columns in hand (or read them in one page-sized query). Nothing else — notably no BalanceJson —
/// so no surface can know more than another.
/// </summary>
/// <param name="AiSuggestedScore"><c>AnalysisResults.AiSuggestedScore</c>.</param>
/// <param name="EnrichmentStatus"><c>AnalysisResults.EnrichmentStatus</c> (a <see cref="BcraEnrichmentOutcome"/> name).</param>
/// <param name="AnalysisKind"><c>Analyses.AnalysisKind</c>.</param>
/// <param name="FiscalYear"><c>Analyses.FiscalYear</c>.</param>
/// <param name="GeneratedAt"><c>AnalysisResults.GeneratedAt</c> (UTC).</param>
/// <param name="RescoredAt"><c>AnalysisResults.RescoredAt</c> (UTC); when set, it is the score's date.</param>
internal sealed record ScoreReliabilityRow(
    decimal?  AiSuggestedScore,
    string?   EnrichmentStatus,
    string?   AnalysisKind,
    int?      FiscalYear,
    DateTime  GeneratedAt,
    DateTime? RescoredAt);

/// <summary>Whether the #300 facts block could be read.</summary>
internal enum ScoreReliabilityFactsState
{
    /// <summary>The key is missing: the row was scored before the facts were recorded.</summary>
    Absent,

    /// <summary>The key is JSON null: building the facts failed when the score was persisted.</summary>
    NotRecorded,

    /// <summary>The breakdown or the block could not be read.</summary>
    Unreadable,

    /// <summary>Read.</summary>
    Present,
}

/// <summary>
/// Everything the evaluator reads, normalised by <see cref="ScoreReliabilityAdapter"/> — the one adapter
/// every surface goes through. Built only there (and by tests).
/// </summary>
internal sealed record ScoreReliabilityInput
{
    public required ScoreReliabilityRow Row { get; init; }

    public ScoreReliabilityFactsState FactsState { get; init; }

    /// <summary>Non-null exactly when <see cref="FactsState"/> is <see cref="ScoreReliabilityFactsState.Present"/>.</summary>
    public ScoreReliabilityFacts? Facts { get; init; }

    /// <summary>True when the breakdown string was present and unreadable.</summary>
    public bool BreakdownUnreadable { get; init; }

    public bool RatiosLowCoverageFallback { get; init; }

    public IReadOnlyDictionary<string, ScoreReliabilityRatioEntry> Ratios { get; init; }
        = new Dictionary<string, ScoreReliabilityRatioEntry>(StringComparer.Ordinal);

    public IReadOnlyList<string> NonComputableRatios { get; init; } = [];

    public bool BcraExcluded { get; init; }

    public string? BcraExclusionReason { get; init; }

    public IReadOnlyList<string> ThresholdFallbacks { get; init; } = [];

    /// <summary>The breakdown's <c>criticalRatios</c> (empty when absent).</summary>
    public IReadOnlyList<string> CriticalRatios { get; init; } = [];

    /// <summary>True when the critical floor actually moved the score (<c>CriticalFloor.WasApplied</c>) and a ratio was critical.</summary>
    public bool CriticalFloorApplied { get; init; }

    /// <summary>The composite before the floor, on the internal 0-100 scale; null when the blob predates it.</summary>
    public decimal? ScoreBeforeFloor { get; init; }
}
