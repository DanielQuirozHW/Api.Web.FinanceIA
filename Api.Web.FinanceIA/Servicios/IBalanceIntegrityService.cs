// Project : Api.Web.FinanceIA
// Created : 2026-09-24

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

// ── Result ──────────────────────────────────────────────────────────────────────

/// <summary>
/// Outcome of <see cref="IBalanceIntegrityService.Evaluate"/>: the report persisted inside
/// BalanceJson, and one es-AR anomaly per failed check for the analysis' anomalies list.
/// </summary>
public sealed record BalanceIntegrityOutcome(BalanceIntegrityReport Report, IReadOnlyList<string> Anomalies);

// ── Service ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Deterministic integrity checks over the balance figures one extraction pass produced — the
/// check the model's self-reported <c>balance_check.balanced</c> cannot be: two wrong subtotals
/// that still add up to the right total pass A = P + PN, but not the sum of their own rubros nor
/// a comparison against the prior-year column.
///
/// Flag only: it never corrects a figure, never touches the score and never throws for a missing
/// figure (a check whose operands were not extracted is skipped).
/// </summary>
public interface IBalanceIntegrityService
{
    /// <summary>
    /// Runs every applicable check over <paramref name="balance"/>, whose figures must already be
    /// in pesos (scaled from the printed unit).
    /// </summary>
    BalanceIntegrityOutcome Evaluate(ExtractedBalance balance);
}
