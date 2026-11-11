// Project : Api.Web.FinanceIA
// Created : 2026-10-22

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One re-score, ready to persist: the new value of every AnalysisResults column a re-score
/// overwrites, the concurrency expectation, and the history row recording the previous values.
/// </summary>
/// <remarks>
/// The columns listed here are the ONLY ones a re-score touches. RiskAlerts, Anomalies,
/// ExecutiveSummary, CatalogueAlerts, ValidationNotes, the AI hashes and tokens and GeneratedAt are
/// extraction outputs and have no slot here, by construction. BalanceJson and ValidationStatus are written
/// only by a balance correction, through <see cref="Correction"/>, together with its AnalysisEdits rows —
/// a plain re-score never touches either.
/// </remarks>
public sealed record AnalysisRescoreWrite
{
    public required Guid     ResultId             { get; init; }
    public required Guid     AnalysisId           { get; init; }
    public required string   TenantId             { get; init; }

    /// <summary>The RescoreCount read before computing; the UPDATE only applies while it still holds.</summary>
    public required int      ExpectedRescoreCount { get; init; }

    public required DateTime RescoredAt           { get; init; }
    public required Guid     RescoredBy           { get; init; }

    public string?           RatiosJson           { get; init; }
    public string?           BcraDataJson         { get; init; }
    public string?           MacroDataJson        { get; init; }
    public decimal?          AiSuggestedScore     { get; init; }
    public string?           AiSuggestedLevel     { get; init; }
    public string?           ScoreBreakdownJson   { get; init; }
    public string?           ScoringNarrative     { get; init; }
    public string?           LufeDataJson         { get; init; }
    public string?           FiscalDataJson       { get; init; }
    public string?           NosisDataJson        { get; init; }
    public string?           CasfogDataJson       { get; init; }
    public required string   EnrichmentStatus     { get; init; }
    public string?           EnrichmentNotes      { get; init; }

    /// <summary>The append-only history row, inserted in the same transaction as the update.</summary>
    public required AnalysisRescore History       { get; init; }

    /// <summary>
    /// Set only by POST {id}/balance-corrections: the corrected BalanceJson and one [AnalysisEdits] row per
    /// corrected figure, written in the SAME transaction as the update and the history row. Null for a plain
    /// re-score, which never touches BalanceJson.
    /// </summary>
    public BalanceCorrectionWrite? Correction     { get; init; }
}
