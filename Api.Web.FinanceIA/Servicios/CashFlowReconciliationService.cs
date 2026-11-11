// Project : Api.Web.FinanceIA
// Created : 2026-08-06

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

// ── Service ───────────────────────────────────────────────────────────────────

/// <summary>
/// Stateless, deterministic implementation of <see cref="ICashFlowReconciliationService"/>.
/// No DI dependencies — can be instantiated directly in tests.
///
/// It only VALIDATES internal consistency; it never recomputes or overwrites an extracted figure.
/// </summary>
public sealed class CashFlowReconciliationService : ICashFlowReconciliationService
{
    // Stable keys for each reconciliation check (used by callers to format the analyst message).
    public const string SectionsVsNetChange = "sections_vs_net_change";
    public const string BeginningPlusChangeVsEnd = "beginning_plus_change_vs_end";

    // Tolerance is deliberately TIGHTER than the ratio-divergence check (which uses 5%). That
    // check compares two independent computation paths (LLM extraction vs deterministic C#), where
    // 5% slack is reasonable. This check compares figures that both come from the SAME document —
    // the statement's own subtotals must add up — so the only legitimate slack is presentation
    // rounding (statements printed in thousands/millions): hence 0.5%. The absolute floor (a
    // currency magnitude, not the 0.05 used for plain ratios) is what, via the AND-gate, suppresses
    // sub-unit rounding noise and the near-zero-expected fallback case.
    private const double RelativeTolerance = 0.005;
    private const double AbsoluteFloor     = 1.0;

    // ── ICashFlowReconciliationService ────────────────────────────────────────

    /// <inheritdoc />
    public IReadOnlyList<CashFlowDivergence> Check(CashFlowData cf)
    {
        var divergences = new List<CashFlowDivergence>();

        // operating + investing + financing (+ monetary result on cash) ≈ net_change.
        // The sum terms still require operating/investing/financing/net_change to all be non-null;
        // monetary_result_on_cash is an optional RT6 reconciling line that contributes 0 when the
        // statement has no such line — and, per SeparateMonetaryResultTerm, also 0 when the line was
        // exposed inside a section subtotal (where it is already counted).
        if (cf.Operating is not null && cf.Investing is not null &&
            cf.Financing is not null && cf.NetChange is not null)
        {
            var sections = cf.Operating.Value + cf.Investing.Value + cf.Financing.Value
                         + SeparateMonetaryResultTerm(cf);
            AddIfDiverges(divergences, SectionsVsNetChange, expected: sections, reported: cf.NetChange.Value);
        }

        // cash_beginning + net_change ≈ cash_end
        if (cf.CashBeginning is not null && cf.NetChange is not null && cf.CashEnd is not null)
        {
            var impliedEnd = cf.CashBeginning.Value + cf.NetChange.Value;
            AddIfDiverges(divergences, BeginningPlusChangeVsEnd, expected: impliedEnd, reported: cf.CashEnd.Value);
        }

        return divergences;
    }

    // Flags a divergence only when it clears BOTH gates (same AND-gate shape as the ratio check):
    // the absolute difference exceeds the currency floor AND the relative difference exceeds
    // RelativeTolerance (0.5% — this comment used to say 5%, the ratio check's looser gate).
    private static void AddIfDiverges(List<CashFlowDivergence> acc, string check, double expected, double reported)
    {
        var absDiff = Math.Abs(expected - reported);
        var relDiff = expected != 0 ? absDiff / Math.Abs(expected) : absDiff;

        if (absDiff > AbsoluteFloor && relDiff > RelativeTolerance)
            acc.Add(new CashFlowDivergence(check, expected, reported));
    }

    // The amount of monetary_result_on_cash that must be added to the three section subtotals as a
    // SEPARATE reconciling term. It is a separate term only when the balance exposed the RECPAM line
    // OUTSIDE the three activity subtotals. When it was folded into a section subtotal (e.g. inside
    // financing), its amount is already counted there — adding it again would double-count and
    // spuriously flag a statement that reconciles on its own. A null/absent section is the pre-2.6
    // contract, under which the amount was ONLY ever populated for the outside case: treat it as
    // "outside" so historical analyses reconcile byte-identically. Shared by Check and Reconcile so
    // the expected sum cannot drift between them.
    private static double SeparateMonetaryResultTerm(CashFlowData cf)
    {
        var exposedInsideSection =
            cf.MonetaryResultOnCashSection is "operating" or "investing" or "financing";
        return exposedInsideSection ? 0 : (cf.MonetaryResultOnCash ?? 0);
    }

    /// <inheritdoc />
    public CashFlowReconciliationResult Reconcile(CashFlowData cf)
    {
        // Pass/fail is DERIVED from Check(...) so the tolerance gate (AddIfDiverges) stays the single
        // source of truth and cannot drift. Here we only recompute the sums needed to surface the
        // figures, and use the same null-guards Check uses to distinguish evaluated from not-evaluated.
        var failed = new HashSet<string>();
        foreach (var d in Check(cf))
            failed.Add(d.Check);

        // sections_vs_net_change: expected = sum of sections (+ monetary result), reported = net_change.
        CashFlowCheckResult sections;
        if (cf.Operating is not null && cf.Investing is not null &&
            cf.Financing is not null && cf.NetChange is not null)
        {
            var sum = cf.Operating.Value + cf.Investing.Value + cf.Financing.Value
                    + SeparateMonetaryResultTerm(cf);
            sections = new CashFlowCheckResult(
                SectionsVsNetChange,
                failed.Contains(SectionsVsNetChange) ? ReconciliationState.Failed : ReconciliationState.Passed,
                sum, cf.NetChange.Value);
        }
        else
        {
            sections = new CashFlowCheckResult(SectionsVsNetChange, ReconciliationState.NotEvaluated, null, null);
        }

        // beginning_plus_change_vs_end: expected = cash_beginning + net_change, reported = cash_end.
        CashFlowCheckResult beginning;
        if (cf.CashBeginning is not null && cf.NetChange is not null && cf.CashEnd is not null)
        {
            var impliedEnd = cf.CashBeginning.Value + cf.NetChange.Value;
            beginning = new CashFlowCheckResult(
                BeginningPlusChangeVsEnd,
                failed.Contains(BeginningPlusChangeVsEnd) ? ReconciliationState.Failed : ReconciliationState.Passed,
                impliedEnd, cf.CashEnd.Value);
        }
        else
        {
            beginning = new CashFlowCheckResult(BeginningPlusChangeVsEnd, ReconciliationState.NotEvaluated, null, null);
        }

        return new CashFlowReconciliationResult(sections, beginning);
    }
}
