// Project : Api.Web.FinanceIA
// Created : 2026-08-06

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

// ── Interface ─────────────────────────────────────────────────────────────────

/// <summary>
/// Validates the internal consistency of an extracted cash flow statement. It VALIDATES ONLY —
/// there is no independent source to recompute cash flow from, so it never overwrites an extracted
/// figure; it only reports the checks that fail to reconcile.
/// </summary>
public interface ICashFlowReconciliationService
{
    /// <summary>
    /// Checks the extracted cash flow figures for internal consistency and returns one
    /// <see cref="CashFlowDivergence"/> per failing check. Each check is skipped when any of its
    /// inputs is null. Returns an empty list when everything reconciles (or nothing is checkable).
    /// </summary>
    /// <param name="cashFlow">The extracted cash flow snapshot (figures shown as-extracted).</param>
    IReadOnlyList<CashFlowDivergence> Check(CashFlowData cashFlow);

    /// <summary>
    /// Evaluates the same two reconciliation checks as <see cref="Check"/> and returns a structured,
    /// per-check outcome (passed / failed / not-evaluated) plus the figures that make each mismatch
    /// legible. Unlike <see cref="Check"/> (which only lists failures) this reports EVERY check,
    /// including passing ones, so a consumer can distinguish "passed" from "was not checked".
    /// Purely additive: the pass/fail decision is derived from <see cref="Check"/>, so the tolerance
    /// gate and the check arithmetic remain in exactly one place and cannot drift.
    /// </summary>
    /// <param name="cashFlow">The extracted cash flow snapshot (figures shown as-extracted).</param>
    CashFlowReconciliationResult Reconcile(CashFlowData cashFlow);
}

/// <summary>Outcome of a single reconciliation check.</summary>
public enum ReconciliationState
{
    /// <summary>The check could not run because one or more of its inputs was null.</summary>
    NotEvaluated,

    /// <summary>The check ran and the figures reconcile within tolerance.</summary>
    Passed,

    /// <summary>The check ran and the figures do not reconcile.</summary>
    Failed,
}

/// <summary>
/// Structured result of one reconciliation check. <see cref="Expected"/> and <see cref="Reported"/>
/// are populated whenever the check was evaluated — including when it passed — so a reader can always
/// see the arithmetic; they are <c>null</c> only when <see cref="State"/> is
/// <see cref="ReconciliationState.NotEvaluated"/>. The meaning of Expected vs Reported depends on the
/// check (identified by <see cref="Check"/>):
/// <list type="bullet">
/// <item><description><c>sections_vs_net_change</c>: Expected = sum of the sections (operating +
/// investing + financing, plus monetary_result_on_cash when present); Reported = net_change as
/// stated.</description></item>
/// <item><description><c>beginning_plus_change_vs_end</c>: Expected = cash_beginning + net_change;
/// Reported = cash_end as stated.</description></item>
/// </list>
/// </summary>
/// <param name="Check">Stable key identifying the check (matches the keys used by <see cref="Check"/>).</param>
/// <param name="State">Whether the check passed, failed, or was not evaluated.</param>
/// <param name="Expected">Value implied by summing the extracted figures; null when not evaluated.</param>
/// <param name="Reported">Total reported by the statement; null when not evaluated.</param>
public sealed record CashFlowCheckResult(
    string Check, ReconciliationState State, double? Expected, double? Reported);

/// <summary>
/// Structured outcome of both cash-flow reconciliation checks. Both members are always present, so a
/// consumer never has to guess whether a check was omitted.
/// </summary>
/// <param name="SectionsVsNetChange">The sections-sum vs net_change check.</param>
/// <param name="BeginningPlusChangeVsEnd">The cash_beginning + net_change vs cash_end check.</param>
public sealed record CashFlowReconciliationResult(
    CashFlowCheckResult SectionsVsNetChange,
    CashFlowCheckResult BeginningPlusChangeVsEnd);

/// <summary>
/// A single failed cash-flow reconciliation check. <see cref="Expected"/> is the value implied by
/// summing the extracted figures; <see cref="Reported"/> is the total the statement itself reports.
/// Both are surfaced to the analyst as-extracted — the divergence is reported, never corrected.
/// </summary>
/// <param name="Check">Stable key identifying which reconciliation failed.</param>
/// <param name="Expected">Value implied by the summed extracted figures.</param>
/// <param name="Reported">Total reported by the statement.</param>
public sealed record CashFlowDivergence(string Check, double Expected, double Reported);
