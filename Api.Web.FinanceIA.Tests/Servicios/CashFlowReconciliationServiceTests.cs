// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-06

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Covers the deterministic cash-flow reconciliation: it VALIDATES internal consistency only and
/// never recomputes/overwrites a figure. Checks: operating+investing+financing ≈ net_change, and
/// cash_beginning+net_change ≈ cash_end. Flags only when absDiff &gt; 1.0 AND relDiff &gt; 5%; each
/// check is skipped when any of its inputs is null.
/// </summary>
public sealed class CashFlowReconciliationServiceTests
{
    private readonly CashFlowReconciliationService _sut = new();

    [Fact]
    public void Check_WhenBothReconcile_ReturnsNoDivergences()
    {
        var cf = new CashFlowData
        {
            Present       = true,
            Operating     = 100, Investing = -40, Financing = -30,  // sum = 30
            NetChange     = 30,
            CashBeginning = 200, CashEnd = 230,                     // 200 + 30 = 230
        };

        Assert.Empty(_sut.Check(cf));
    }

    [Fact]
    public void Check_WhenSectionsDoNotSumToNetChange_FlagsSectionsCheckOnly()
    {
        var cf = new CashFlowData
        {
            Present   = true,
            Operating = 100, Investing = -40, Financing = -30,      // sum = 30
            NetChange = 50,                                         // reported 50 → diff 20
            // cash beginning/end left null → second check skipped
        };

        var result = _sut.Check(cf);

        var d = Assert.Single(result);
        Assert.Equal(CashFlowReconciliationService.SectionsVsNetChange, d.Check);
        Assert.Equal(30.0, d.Expected, 3);
        Assert.Equal(50.0, d.Reported, 3);
    }

    [Fact]
    public void Check_WhenBeginningPlusChangeDoesNotEqualEnd_FlagsCashCheckOnly()
    {
        var cf = new CashFlowData
        {
            Present       = true,
            CashBeginning = 200, NetChange = 30, CashEnd = 300,     // implied 230 vs reported 300
            // operating/investing/financing left null → first check skipped
        };

        var result = _sut.Check(cf);

        var d = Assert.Single(result);
        Assert.Equal(CashFlowReconciliationService.BeginningPlusChangeVsEnd, d.Check);
        Assert.Equal(230.0, d.Expected, 3);
        Assert.Equal(300.0, d.Reported, 3);
    }

    [Fact]
    public void Check_WhenWithinTightRelativeTolerance_ReturnsNoDivergences()
    {
        // relDiff = 3/1000 = 0.3% (< 0.5%) → not flagged: presentation-rounding slack only.
        var cf = new CashFlowData
        {
            Present   = true,
            Operating = 1000, Investing = 0, Financing = 0,         // sum = 1000
            NetChange = 997,
        };

        Assert.Empty(_sut.Check(cf));
    }

    [Fact]
    public void Check_WhenRelativeDiffExceedsTightTolerance_Flags()
    {
        // relDiff = 10/1000 = 1% — within the old 5% ratio tolerance, but exceeds the tightened
        // 0.5% same-document tolerance (absDiff = 10 > floor), so it must now flag.
        var cf = new CashFlowData
        {
            Present   = true,
            Operating = 1000, Investing = 0, Financing = 0,         // sum = 1000
            NetChange = 990,
        };

        var d = Assert.Single(_sut.Check(cf));
        Assert.Equal(CashFlowReconciliationService.SectionsVsNetChange, d.Check);
    }

    [Fact]
    public void Check_WhenBelowAbsoluteCurrencyFloor_ReturnsNoDivergences()
    {
        // relDiff = 0.8/10 = 8% (> 5%) but absDiff = 0.8 (< 1.0 floor) → not flagged.
        // This is the case the currency floor exists for (sub-unit rounding, not a real mismatch).
        var cf = new CashFlowData
        {
            Present   = true,
            Operating = 10, Investing = 0, Financing = 0,           // sum = 10
            NetChange = 9.2,
        };

        Assert.Empty(_sut.Check(cf));
    }

    [Fact]
    public void Check_WhenAnInputIsNull_SkipsThatCheck()
    {
        var cf = new CashFlowData
        {
            Present   = true,
            Operating = null, Investing = -40, Financing = -30,     // operating null → check skipped
            NetChange = 50,
            // cash beginning/end null → cash check skipped
        };

        Assert.Empty(_sut.Check(cf));
    }

    [Fact]
    public void Check_WhenNotPresentAllNull_ReturnsNoDivergences()
    {
        Assert.Empty(_sut.Check(new CashFlowData { Present = false }));
    }

    [Fact]
    public void Check_WhenBothChecksDiverge_FlagsBoth()
    {
        var cf = new CashFlowData
        {
            Present       = true,
            Operating     = 100, Investing = -40, Financing = -30,  // sum 30 vs net_change 60
            NetChange     = 60,
            CashBeginning = 200, CashEnd = 400,                     // implied 260 vs reported 400
        };

        var result = _sut.Check(cf);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, d => d.Check == CashFlowReconciliationService.SectionsVsNetChange);
        Assert.Contains(result, d => d.Check == CashFlowReconciliationService.BeginningPlusChangeVsEnd);
    }

    [Fact]
    public void Check_WhenMonetaryResultOnCashClosesReconciliation_NoDivergence()
    {
        // RT6 adjusted statement: the three activity sections sum to 30, but the monetary result
        // on cash (+20) is a separate reconciling line. Four-term sum = 50 = net_change → OK.
        // Without including that line the three-term sum (30) would falsely flag as non-reconciling.
        var cf = new CashFlowData
        {
            Present              = true,
            Operating            = 100, Investing = -40, Financing = -30,  // three-term sum = 30
            MonetaryResultOnCash = 20,
            NetChange            = 50,
        };

        Assert.Empty(_sut.Check(cf));
    }

    [Fact]
    public void Check_WhenNoMonetaryLineAndThreeTermsReconcile_NoDivergence()
    {
        // No monetary line (null → contributes 0); three-term sum = 30 = net_change → unchanged.
        var cf = new CashFlowData
        {
            Present   = true,
            Operating = 100, Investing = -40, Financing = -30,     // sum = 30
            NetChange = 30,
        };

        Assert.Empty(_sut.Check(cf));
    }

    [Fact]
    public void Check_WhenMonetaryResultOnCashIsInsideASection_NotDoubleCounted()
    {
        // Agropecuaria Millagro shape: the RECPAM on cash (+20) is exposed INSIDE the financing
        // section, so it is ALREADY part of the -30 financing subtotal. The three activity terms
        // sum to 30 = net_change, i.e. the statement reconciles exactly. The reconciler must NOT
        // add the +20 a second time — doing so computes an expected 50 vs reported 30 and would
        // spuriously flag a statement that closes on its own. This is the whole risk of Option A.
        var cf = new CashFlowData
        {
            Present                     = true,
            Operating                   = 100, Investing = -40, Financing = -30, // three-term sum = 30
            MonetaryResultOnCash        = 20,
            MonetaryResultOnCashSection = "financing",   // folded into financing → already counted
            NetChange                   = 30,
        };

        Assert.Empty(_sut.Check(cf));
    }

    [Fact]
    public void Check_WhenMonetaryResultExposedOutside_AddedAsSeparateTerm()
    {
        // Explicit "outside" section behaves exactly like the pre-2.6 null case: the +20 IS a
        // separate reconciling term, so the four-term sum (30 + 20 = 50) equals net_change.
        var cf = new CashFlowData
        {
            Present                     = true,
            Operating                   = 100, Investing = -40, Financing = -30, // three-term sum = 30
            MonetaryResultOnCash        = 20,
            MonetaryResultOnCashSection = "outside",     // standalone reconciling line → added
            NetChange                   = 50,
        };

        Assert.Empty(_sut.Check(cf));
    }

    [Fact]
    public void Check_WhenMonetaryResultSectionNullLegacy_TreatedAsOutside()
    {
        // Pre-2.6 analyses carry the amount with NO section. It must still be added (outside
        // behaviour) so historical verdicts are unchanged: three-term sum 30 + 20 = 50 = net_change.
        var cf = new CashFlowData
        {
            Present                     = true,
            Operating                   = 100, Investing = -40, Financing = -30,
            MonetaryResultOnCash        = 20,
            MonetaryResultOnCashSection = null,          // legacy: no section persisted
            NetChange                   = 50,
        };

        Assert.Empty(_sut.Check(cf));
    }

    // ── Reconcile (structured outcome; passed / failed / not_evaluated) ────────────

    [Fact]
    public void Reconcile_WhenBothReconcile_ReportsBothPassedWithFigures()
    {
        var cf = new CashFlowData
        {
            Present       = true,
            Operating     = 100, Investing = -40, Financing = -30,  // sum = 30
            NetChange     = 30,
            CashBeginning = 200, CashEnd = 230,                     // 200 + 30 = 230
        };

        var result = _sut.Reconcile(cf);

        Assert.Equal(ReconciliationState.Passed, result.SectionsVsNetChange.State);
        Assert.Equal(30.0, result.SectionsVsNetChange.Expected!.Value, 3);
        Assert.Equal(30.0, result.SectionsVsNetChange.Reported!.Value, 3);

        Assert.Equal(ReconciliationState.Passed, result.BeginningPlusChangeVsEnd.State);
        Assert.Equal(230.0, result.BeginningPlusChangeVsEnd.Expected!.Value, 3);
        Assert.Equal(230.0, result.BeginningPlusChangeVsEnd.Reported!.Value, 3);
    }

    [Fact]
    public void Reconcile_WhenSectionsDoNotSumToNetChange_ReportsFailedWithBothFigures()
    {
        var cf = new CashFlowData
        {
            Present   = true,
            Operating = 100, Investing = -40, Financing = -30,      // sum = 30
            NetChange = 50,                                         // reported 50 → diff 20
        };

        var result = _sut.Reconcile(cf);

        Assert.Equal(ReconciliationState.Failed, result.SectionsVsNetChange.State);
        Assert.Equal(30.0, result.SectionsVsNetChange.Expected!.Value, 3);
        Assert.Equal(50.0, result.SectionsVsNetChange.Reported!.Value, 3);
    }

    [Fact]
    public void Reconcile_WhenBothChecksDiverge_ReportsBothFailedWithFigures()
    {
        var cf = new CashFlowData
        {
            Present       = true,
            Operating     = 100, Investing = -40, Financing = -30,  // sum 30 vs net_change 60
            NetChange     = 60,
            CashBeginning = 200, CashEnd = 400,                     // implied 260 vs reported 400
        };

        var result = _sut.Reconcile(cf);

        Assert.Equal(ReconciliationState.Failed, result.SectionsVsNetChange.State);
        Assert.Equal(30.0,  result.SectionsVsNetChange.Expected!.Value, 3);
        Assert.Equal(60.0,  result.SectionsVsNetChange.Reported!.Value, 3);

        Assert.Equal(ReconciliationState.Failed, result.BeginningPlusChangeVsEnd.State);
        Assert.Equal(260.0, result.BeginningPlusChangeVsEnd.Expected!.Value, 3);
        Assert.Equal(400.0, result.BeginningPlusChangeVsEnd.Reported!.Value, 3);
    }

    [Fact]
    public void Reconcile_WhenSectionInputMissing_ReportsNotEvaluatedNotPassed()
    {
        // Operating null → sections check cannot run. It must be NotEvaluated (with null figures),
        // NOT Passed — a statement with no operating figure was not checked, it did not reconcile.
        var cf = new CashFlowData
        {
            Present       = true,
            Operating     = null, Investing = -40, Financing = -30,
            NetChange     = 50,
            CashBeginning = 200, CashEnd = 230,                     // cash check still runs: 250 vs 230
        };

        var result = _sut.Reconcile(cf);

        Assert.Equal(ReconciliationState.NotEvaluated, result.SectionsVsNetChange.State);
        Assert.Null(result.SectionsVsNetChange.Expected);
        Assert.Null(result.SectionsVsNetChange.Reported);

        // Cash check has all inputs (200 + 50 = 250 vs 230 → fails), proving only the section check
        // was skipped, not the whole evaluation.
        Assert.Equal(ReconciliationState.Failed, result.BeginningPlusChangeVsEnd.State);
    }

    [Fact]
    public void Reconcile_WhenNotPresentAllNull_ReportsBothNotEvaluated()
    {
        var result = _sut.Reconcile(new CashFlowData { Present = false });

        Assert.Equal(ReconciliationState.NotEvaluated, result.SectionsVsNetChange.State);
        Assert.Null(result.SectionsVsNetChange.Expected);
        Assert.Null(result.SectionsVsNetChange.Reported);

        Assert.Equal(ReconciliationState.NotEvaluated, result.BeginningPlusChangeVsEnd.State);
        Assert.Null(result.BeginningPlusChangeVsEnd.Expected);
        Assert.Null(result.BeginningPlusChangeVsEnd.Reported);
    }

    [Fact]
    public void Reconcile_WhenMonetaryResultInsideSection_PassesAndExpectedExcludesIt()
    {
        // Millagro via Reconcile: the RECPAM (+20) is inside the financing subtotal. Expected must be
        // the three-term sum (30) that the Portal shows on the card — NOT 50 — and the check passes.
        var cf = new CashFlowData
        {
            Present                     = true,
            Operating                   = 100, Investing = -40, Financing = -30,
            MonetaryResultOnCash        = 20,
            MonetaryResultOnCashSection = "financing",
            NetChange                   = 30,
        };

        var result = _sut.Reconcile(cf);

        Assert.Equal(ReconciliationState.Passed, result.SectionsVsNetChange.State);
        Assert.Equal(30.0, result.SectionsVsNetChange.Expected!.Value, 3);
        Assert.Equal(30.0, result.SectionsVsNetChange.Reported!.Value, 3);
    }

    [Fact]
    public void Reconcile_WhenMonetaryResultOutside_PassesAndExpectedIncludesIt()
    {
        // Outside reconciling line: Expected must be the four-term sum (30 + 20 = 50) = net_change.
        var cf = new CashFlowData
        {
            Present                     = true,
            Operating                   = 100, Investing = -40, Financing = -30,
            MonetaryResultOnCash        = 20,
            MonetaryResultOnCashSection = "outside",
            NetChange                   = 50,
        };

        var result = _sut.Reconcile(cf);

        Assert.Equal(ReconciliationState.Passed, result.SectionsVsNetChange.State);
        Assert.Equal(50.0, result.SectionsVsNetChange.Expected!.Value, 3);
        Assert.Equal(50.0, result.SectionsVsNetChange.Reported!.Value, 3);
    }

    [Theory]
    [MemberData(nameof(DriftGuardCases))]
    public void Reconcile_FailedStateMatchesCheckDivergences(CashFlowData cf)
    {
        // Drift guard: a check is Failed in Reconcile IFF its key appears in Check's divergences.
        var divergenceKeys = _sut.Check(cf).Select(d => d.Check).ToHashSet();
        var result = _sut.Reconcile(cf);

        foreach (var check in new[] { result.SectionsVsNetChange, result.BeginningPlusChangeVsEnd })
        {
            var isFailed  = check.State == ReconciliationState.Failed;
            var hasDiverg = divergenceKeys.Contains(check.Check);
            Assert.Equal(hasDiverg, isFailed);
        }
    }

    public static IEnumerable<object[]> DriftGuardCases() => new[]
    {
        new object[] { new CashFlowData { Present = true, Operating = 100, Investing = -40, Financing = -30, NetChange = 30, CashBeginning = 200, CashEnd = 230 } }, // both pass
        new object[] { new CashFlowData { Present = true, Operating = 100, Investing = -40, Financing = -30, NetChange = 60, CashBeginning = 200, CashEnd = 400 } }, // both fail
        new object[] { new CashFlowData { Present = true, Operating = 100, Investing = -40, Financing = -30, NetChange = 50 } },                                     // section fails, cash n/e
        new object[] { new CashFlowData { Present = true, CashBeginning = 200, NetChange = 30, CashEnd = 300 } },                                                    // cash fails, section n/e
        new object[] { new CashFlowData { Present = false } },                                                                                                      // nothing checkable
        new object[] { new CashFlowData { Present = true, Operating = 100, Investing = -40, Financing = -30, MonetaryResultOnCash = 20, MonetaryResultOnCashSection = "financing", NetChange = 30 } }, // RECPAM inside section, closes
        new object[] { new CashFlowData { Present = true, Operating = 100, Investing = -40, Financing = -30, MonetaryResultOnCash = 20, MonetaryResultOnCashSection = "outside",   NetChange = 50 } }, // RECPAM outside, closes
    };

    // ── Prompt-upgrade invariance (2.5 → 2.6) ──────────────────────────────────────
    // The ONLY cash_flow change 2.6 introduces is that a RECPAM-on-cash line exposed INSIDE a
    // section — previously invisible (monetary_result_on_cash = null) — now carries its amount plus
    // a section tag. At the reconciler that pair contributes exactly what the previous null did (0,
    // because it is already inside the subtotal), so the verdict is unchanged. Outside/none cases
    // are byte-identical across versions. This is the general form of the "provably changes no
    // verdict" guarantee behind the five-balance before/after table; the shapes below are generic,
    // not the real balances' figures.

    [Theory]
    [MemberData(nameof(UpgradeInvariancePairs))]
    public void Reconcile_VerdictUnchangedFromV25ToV26(CashFlowData v25, CashFlowData v26)
    {
        var before = _sut.Reconcile(v25);
        var after  = _sut.Reconcile(v26);

        Assert.Equal(before.SectionsVsNetChange.State,      after.SectionsVsNetChange.State);
        Assert.Equal(before.BeginningPlusChangeVsEnd.State, after.BeginningPlusChangeVsEnd.State);
    }

    public static IEnumerable<object[]> UpgradeInvariancePairs() => new[]
    {
        // Inside-section RECPAM (Millagro shape): 2.5 saw null; 2.6 sees 20/"financing". Both pass.
        new object[]
        {
            new CashFlowData { Present = true, Operating = 100, Investing = -40, Financing = -30, NetChange = 30, CashBeginning = 200, CashEnd = 230 },
            new CashFlowData { Present = true, Operating = 100, Investing = -40, Financing = -30, MonetaryResultOnCash = 20, MonetaryResultOnCashSection = "financing", NetChange = 30, CashBeginning = 200, CashEnd = 230 },
        },
        // Outside reconciling line: 2.5 stored the amount with a null section; 2.6 tags it "outside".
        // Both add the term, both pass — the "outside" tag is behaviourally identical to legacy null.
        new object[]
        {
            new CashFlowData { Present = true, Operating = 100, Investing = -40, Financing = -30, MonetaryResultOnCash = 20, MonetaryResultOnCashSection = null,      NetChange = 50 },
            new CashFlowData { Present = true, Operating = 100, Investing = -40, Financing = -30, MonetaryResultOnCash = 20, MonetaryResultOnCashSection = "outside", NetChange = 50 },
        },
        // No RECPAM line at all: identical in both versions (control).
        new object[]
        {
            new CashFlowData { Present = true, Operating = 100, Investing = -40, Financing = -20, NetChange = 40 },
            new CashFlowData { Present = true, Operating = 100, Investing = -40, Financing = -20, NetChange = 40 },
        },
        // A statement whose sections check FAILS must stay failed across the upgrade (no RECPAM line).
        new object[]
        {
            new CashFlowData { Present = true, Operating = 100, Investing = -40, Financing = -30, NetChange = 90 },
            new CashFlowData { Present = true, Operating = 100, Investing = -40, Financing = -30, NetChange = 90 },
        },
    };
}
