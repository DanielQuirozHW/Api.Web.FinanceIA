// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Xunit;

namespace Api.Web.FinanceIA.Tests.AnalysisRuns;

/// <summary>
/// The one ValidationStatus rule (<see cref="BalanceValidationStatus"/>): the extraction passes the model's
/// balance check AND the integrity result; a balance correction passes the integrity result alone.
/// </summary>
public sealed class BalanceValidationStatusTests
{
    [Theory]
    // The extraction: the model's check is always present.
    [InlineData(true,  true,  "Passed")]
    [InlineData(true,  null,  "Passed")]   // integrity unavailable keeps the old rule
    [InlineData(true,  false, "Warning")]
    [InlineData(false, true,  "Warning")]
    [InlineData(false, null,  "Warning")]
    [InlineData(false, false, "Warning")]
    // A correction: integrity alone decides, and no evidence is not a pass.
    [InlineData(null,  true,  "Passed")]
    [InlineData(null,  false, "Warning")]
    [InlineData(null,  null,  "Warning")]
    public void Derive_truth_table(bool? modelBalanceCheck, bool? integrityPassed, string expected)
        => Assert.Equal(expected, BalanceValidationStatus.Derive(modelBalanceCheck, integrityPassed));

    /// <summary>The extraction's flag is exactly the expression the pipeline used before the helper existed.</summary>
    [Theory]
    [InlineData(true,  0.0)]
    [InlineData(true,  5.0)]
    [InlineData(false, 0.0)]
    [InlineData(false, 0.09)]
    [InlineData(false, 0.1)]
    [InlineData(false, 3.5)]
    public void The_pipeline_rule_is_unchanged(bool balanced, double differencePct)
    {
        foreach (var integrity in new bool?[] { true, false, null })
        {
            var before = (balanced || differencePct < 0.1) && integrity != false ? "Passed" : "Warning";
            var after  = BalanceValidationStatus.Derive(
                BalanceValidationStatus.ModelBalanceCheck(balanced, differencePct), integrity);

            Assert.Equal(before, after);
        }
    }

    /// <summary>
    /// Reliability policy 5: a report whose ONLY failures are presentation checks (net_profit_reconciliation)
    /// counts as holding — the balance sheet adds up. Any other failure, or unknown names, keeps the report's verdict.
    /// </summary>
    // The column-mix column (policy 6) was added with null — "could not be checked" — on every pre-existing row, which
    // is exactly the input those rows always had; the rows after them pin the mix itself.
    [Theory]
    [InlineData(false, new[] { BalanceIntegrityService.NetProfitReconciliationCheck }, false, null,  true)]
    [InlineData(false, new[] { BalanceIntegrityService.NetProfitReconciliationCheck }, true,  null,  false)]   // sign flip: a misread
    [InlineData(false, new[] { BalanceIntegrityService.NetProfitReconciliationCheck }, null,  null,  false)]   // flip unknown
    [InlineData(false, new[] { BalanceIntegrityService.NetProfitReconciliationCheck, BalanceIntegrityService.AccountingEquationCheck }, false, null, false)]
    [InlineData(false, new[] { BalanceIntegrityService.ColumnSwapCheck }, null, null, false)]
    [InlineData(false, null, null, null, false)]
    [InlineData(true,  new string[0], null, null, true)]
    [InlineData(null,  null, null, null, null)]
    [InlineData(false, new[] { BalanceIntegrityService.NetProfitReconciliationCheck }, false, true,  false)]   // column mix: a misread
    [InlineData(false, new[] { BalanceIntegrityService.NetProfitReconciliationCheck }, false, false, true)]    // checked, no mix
    [InlineData(false, new[] { BalanceIntegrityService.NetProfitReconciliationCheck }, true,  true,  false)]
    public void IntegrityEvidence_reads_a_presentation_only_failure_as_holding(
        bool? passed, string[]? failed, bool? signFlip, bool? columnMix, bool? expected)
        => Assert.Equal(expected, BalanceValidationStatus.IntegrityEvidence(passed, failed, signFlip, columnMix));

    [Fact]
    public void A_presentation_only_failure_passes_both_the_extraction_and_a_correction()
    {
        var evidence = BalanceValidationStatus.IntegrityEvidence(false, [BalanceIntegrityService.NetProfitReconciliationCheck], false, null);

        Assert.Equal(BalanceValidationStatus.Passed,  BalanceValidationStatus.Derive(true, evidence));
        Assert.Equal(BalanceValidationStatus.Passed,  BalanceValidationStatus.Derive(null, evidence));
        Assert.Equal(BalanceValidationStatus.Warning, BalanceValidationStatus.Derive(false, evidence));

        var flipped = BalanceValidationStatus.IntegrityEvidence(false, [BalanceIntegrityService.NetProfitReconciliationCheck], true, null);
        Assert.Equal(BalanceValidationStatus.Warning, BalanceValidationStatus.Derive(true, flipped));
        Assert.Equal(BalanceValidationStatus.Warning, BalanceValidationStatus.Derive(null, flipped));

        // Policy 6: a column mix is a misread too, on both writers.
        var mixed = BalanceValidationStatus.IntegrityEvidence(false, [BalanceIntegrityService.NetProfitReconciliationCheck], false, true);
        Assert.Equal(BalanceValidationStatus.Warning, BalanceValidationStatus.Derive(true, mixed));
        Assert.Equal(BalanceValidationStatus.Warning, BalanceValidationStatus.Derive(null, mixed));
    }
}
