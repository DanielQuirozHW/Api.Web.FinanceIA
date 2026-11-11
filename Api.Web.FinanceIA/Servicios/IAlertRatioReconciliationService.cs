// Project : Api.Web.FinanceIA
// Created : 2026-08-09

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

// ── Result ──────────────────────────────────────────────────────────────────────

/// <summary>
/// Outcome of reconciling a single AI-authored alert description against the
/// balance-sheet denominator its own text names.
/// </summary>
/// <param name="Description">
/// The description, with the percentage corrected when a contradiction was proven;
/// otherwise the original text, unchanged.
/// </param>
/// <param name="Note">
/// Spanish anomaly note describing the correction, or <c>null</c> when nothing changed.
/// </param>
/// <param name="Corrected">
/// <c>true</c> only when a mislabeled denominator was proven and the number was rewritten.
/// </param>
public sealed record AlertDescriptionReconciliation(string Description, string? Note, bool Corrected)
{
    /// <summary>Percentage the model stated in the text (0 when nothing changed).</summary>
    public double StatedPercent { get; init; }

    /// <summary>Percentage recomputed against the stated denominator (0 when nothing changed).</summary>
    public double ExpectedPercent { get; init; }

    /// <summary>Denominator phrase the text named, e.g. "total del pasivo" (null when nothing changed).</summary>
    public string? Denominator { get; init; }

    /// <summary>Raw matched clause text, e.g. "22,6% del total del pasivo" (null when nothing changed).</summary>
    public string? Clause { get; init; }
}

/// <summary>
/// Outcome of reconciling every "X% del &lt;denominador&gt;" clause found in a block of free-form
/// text (e.g. <c>executive_summary</c>, or an anomaly string), as opposed to
/// <see cref="AlertDescriptionReconciliation"/> which validates a single alert's one clause. Never
/// guesses: a clause whose numerator or denominator cannot be proven is left completely untouched.
/// </summary>
/// <param name="Text">The text, with each provably-wrong clause's percentage corrected; otherwise
/// the original text, unchanged.</param>
/// <param name="Corrections">One entry per clause that was corrected, in the order they appear in
/// <paramref name="Text"/>. Empty when nothing changed.</param>
/// <param name="UnresolvedClauses">One entry per clause whose numerator (no currency amount found
/// in its own sentence) or denominator (unrecognized phrase, or a trailing denominator amount that
/// disagrees with the extracted figures beyond tolerance) could not be resolved — the clause's own
/// verbatim matched text (e.g. "55,4% del activo total"), in the order clauses were scanned. A
/// clause that WAS successfully checked and found already correct is never listed here — only
/// clauses that could be neither corrected nor confirmed. Empty when every clause found was
/// resolved (corrected or already correct) or when no clause was found at all.</param>
public sealed record NarrativeReconciliation(
    string Text,
    IReadOnlyList<NarrativeClauseCorrection> Corrections,
    IReadOnlyList<string> UnresolvedClauses);

/// <summary>One corrected clause within a <see cref="NarrativeReconciliation"/>.</summary>
public sealed record NarrativeClauseCorrection(
    double StatedPercent, double ExpectedPercent, string Denominator, string Clause);

// ── Contract ────────────────────────────────────────────────────────────────────

/// <summary>
/// Deterministic, presentation-only cross-check for the percentages an AI model writes
/// into an alert description. When a description states "X% del &lt;denominador&gt;" and the
/// value does not match that named denominator (computed from figures already extracted),
/// the number is corrected so it agrees with its own label. Never touches scoring, weights
/// or thresholds — purely fixes what the analyst reads.
/// </summary>
public interface IAlertRatioReconciliationService
{
    /// <summary>
    /// Validates the percentage in <paramref name="description"/> against the balance-sheet
    /// denominator its text names. Fail-safe: returns the input unchanged unless a
    /// contradiction is proven.
    /// </summary>
    AlertDescriptionReconciliation Reconcile(string description, BalanceAccounts accounts);

    /// <summary>
    /// Same fix as <see cref="Reconcile"/>, generalised to every percentage clause found in
    /// free-form narrative text (e.g. <c>executive_summary</c>). Each clause resolves
    /// independently against the closest preceding currency amount; a clause that cannot be
    /// proven correct or incorrect is left untouched — never guess.
    /// </summary>
    NarrativeReconciliation ReconcileNarrative(string text, BalanceAccounts accounts);
}
