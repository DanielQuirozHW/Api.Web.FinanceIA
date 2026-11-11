// Project : Api.Web.FinanceIA
// Created : 2026-11-03

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>
/// AnalysisResults.ValidationStatus — the balance-sheet arithmetic flag — derived the ONE way both writers use: the
/// extraction (<see cref="AnalysisPipeline"/>) and a balance correction (<see cref="AnalysisRescoreService"/>). Only
/// <see cref="Passed"/> and <see cref="Warning"/> are produced, both admitted by CK_AnalysisResults_ValidationStatus.
/// </summary>
/// <remarks>
/// Two pieces of evidence, each true (holds), false (fails) or null (absent): the model's self-reported balance
/// check and the deterministic integrity report. The flag is <see cref="Passed"/> when NEITHER fails and AT LEAST
/// ONE holds. The extraction always has the model's check, so for it this is exactly the rule it always had — the
/// check holds and integrity did not fail (a null integrity, an older output or a failed evaluation, keeps the old
/// rule). A correction passes null for the model's check — the stored <c>balanced</c>/<c>difference_pct</c> describe
/// the ORIGINAL reading and a correction never rewrites them — so for it integrity alone decides, and an integrity
/// that could not be re-evaluated is a <see cref="Warning"/>, never a pass on no evidence.
/// </remarks>
internal static class BalanceValidationStatus
{
    /// <summary>The balance adds up.</summary>
    public const string Passed = "Passed";

    /// <summary>The balance does not add up, or nothing showed that it does.</summary>
    public const string Warning = "Warning";

    /// <summary>The model's own balance_check: it called the balance balanced, or off by less than 0.1 %.</summary>
    public static bool ModelBalanceCheck(bool balanced, double differencePct)
        => balanced || differencePct < 0.1;

    /// <summary>
    /// The integrity evidence the flag reads: the report's <paramref name="passed"/>, except that a report whose
    /// ONLY failures are presentation checks (<see cref="BalanceIntegrityService.FailsOnlyOnPresentation"/> —
    /// net_profit_reconciliation, an income-statement presentation) counts as holding: this column is the
    /// balance-SHEET arithmetic flag, and those figures add up. A sign-flipped net profit or a column mix (misreads),
    /// an unknown flip, and unknown names, keep <paramref name="passed"/>.
    /// </summary>
    public static bool? IntegrityEvidence(
        bool? passed, IEnumerable<string>? failedChecks, bool? netProfitSignFlip, bool? netProfitColumnMix)
        => passed == false && BalanceIntegrityService.FailsOnlyOnPresentation(failedChecks, netProfitSignFlip, netProfitColumnMix)
            ? true
            : passed;

    /// <summary>The flag for the given evidence; see the remarks.</summary>
    public static string Derive(bool? modelBalanceCheck, bool? integrityPassed)
        => modelBalanceCheck != false
        && integrityPassed != false
        && (modelBalanceCheck == true || integrityPassed == true)
            ? Passed
            : Warning;
}
