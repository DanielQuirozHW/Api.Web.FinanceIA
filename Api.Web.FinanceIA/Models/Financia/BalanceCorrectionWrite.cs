// Project : Api.Web.FinanceIA
// Created : 2026-11-02

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The balance half of a correction's write (<see cref="AnalysisRescoreWrite.Correction"/>): the corrected
/// BalanceJson, the ValidationStatus recomputed from its re-evaluated integrity, and its append-only audit rows.
/// </summary>
public sealed record BalanceCorrectionWrite
{
    /// <summary>The whole corrected BalanceJson, with <c>manual_corrections</c> and the rewritten <c>integrity</c>.</summary>
    public required string                    BalanceJson      { get; init; }

    /// <summary>
    /// AnalysisResults.ValidationStatus for the corrected balance — <c>Passed</c> when its re-evaluated integrity
    /// passes, <c>Warning</c> otherwise — by the same rule the extraction uses (BalanceValidationStatus).
    /// </summary>
    public required string                    ValidationStatus { get; init; }

    /// <summary>One row per corrected figure.</summary>
    public required IReadOnlyList<AnalysisEdit> Edits          { get; init; }
}
