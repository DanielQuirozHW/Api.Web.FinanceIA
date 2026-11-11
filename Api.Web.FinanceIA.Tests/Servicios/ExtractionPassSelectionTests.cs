// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The pure pass-selection rule (<see cref="ClaudeService.ChoosePass"/>), the structure-check set
/// that triggers a second pass, and the read-time <see cref="ExtractionIntegrityReader"/>.
/// </summary>
public sealed class ExtractionPassSelectionTests
{
    private const string CurrentAssetsItems = BalanceIntegrityService.CurrentAssetsItemsCheck;
    private const string CurrentLiabItems   = BalanceIntegrityService.CurrentLiabilitiesItemsCheck;
    private const string AssetsComponents   = BalanceIntegrityService.AssetsComponentsCheck;
    private const string NetProfit          = BalanceIntegrityService.NetProfitReconciliationCheck;

    private static BalanceIntegrityReport Report(params (string Name, bool Passed)[] checks) => new()
    {
        Passed         = checks.All(c => c.Passed),
        Checks         = checks.Select(c => new BalanceIntegrityCheck { Name = c.Name, Passed = c.Passed }).ToList(),
        Skipped        = [],
        AffectedRatios = [],
    };

    // A report whose net_profit_reconciliation failed with the given column-mix verdict, beside passing structure checks.
    private static BalanceIntegrityReport NetProfitFailed(bool? columnMix, params (string Name, bool Passed)[] others)
    {
        var checks = others.Select(c => new BalanceIntegrityCheck { Name = c.Name, Passed = c.Passed }).ToList();
        checks.Add(new BalanceIntegrityCheck { Name = NetProfit, Passed = false, ColumnMix = columnMix });
        return new BalanceIntegrityReport { Passed = false, Checks = checks, Skipped = [], AffectedRatios = [] };
    }

    // ── ChoosePass ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void A_column_mix_loses_to_a_pass_that_evaluated_net_profit_without_one(bool? otherVerdict)
    {
        // Both passes fail net_profit_reconciliation and nothing structural, at the same capped confidence: before
        // policy 6 the tie kept the first (mixed) pass.
        var mixed = NetProfitFailed(true,         (AssetsComponents, true));
        var other = NetProfitFailed(otherVerdict, (AssetsComponents, true));

        Assert.Equal(new ClaudeService.PassChoice(true,  "no_column_mix"), ClaudeService.ChoosePass(mixed, 0.69m, other, 0.69m));
        Assert.Equal(new ClaudeService.PassChoice(false, "no_column_mix"), ClaudeService.ChoosePass(other, 0.40m, mixed, 0.69m));
    }

    [Fact]
    public void A_clean_second_pass_beats_a_mixed_first_on_integrity_as_before()
    {
        var mixed = NetProfitFailed(true, (AssetsComponents, true));
        var clean = Report((AssetsComponents, true), (NetProfit, true));

        Assert.Equal(new ClaudeService.PassChoice(true, "integrity_passed"), ClaudeService.ChoosePass(mixed, 0.69m, clean, 0.40m));
    }

    [Fact]
    public void A_pass_that_skipped_net_profit_does_not_win_on_the_column_mix()
    {
        // The other pass failed a sign check and never ran net_profit_reconciliation: no evidence it reads the income
        // statement better, so the column-mix rule does not decide and the existing rules do.
        var mixed   = NetProfitFailed(true, (AssetsComponents, true));
        var skipped = Report((AssetsComponents, true), (BalanceIntegrityService.SignInventoryCheck, false));

        Assert.Equal(new ClaudeService.PassChoice(false, "tie_first"), ClaudeService.ChoosePass(mixed, 0.69m, skipped, 0.69m));
        Assert.Equal(new ClaudeService.PassChoice(true, "higher_confidence"), ClaudeService.ChoosePass(mixed, 0.50m, skipped, 0.69m));
    }

    [Fact]
    public void Fewer_structure_failures_still_outrank_the_column_mix()
    {
        var mixedButAddsUp     = NetProfitFailed(true,  (AssetsComponents, true));
        var unmixedButMisreads = NetProfitFailed(false, (AssetsComponents, false));

        Assert.Equal(new ClaudeService.PassChoice(false, "fewer_structure_failures"),
            ClaudeService.ChoosePass(mixedButAddsUp, 0.69m, unmixedButMisreads, 0.69m));
    }

    [Fact]
    public void Both_passes_mixed_fall_through_to_the_existing_rules()
    {
        var first  = NetProfitFailed(true, (AssetsComponents, true));
        var second = NetProfitFailed(true, (AssetsComponents, true));

        Assert.Equal(new ClaudeService.PassChoice(false, "tie_first"), ClaudeService.ChoosePass(first, 0.69m, second, 0.69m));
    }

    [Fact]
    public void Integrity_passed_beats_failed_whatever_the_confidence()
    {
        var failed = Report((AssetsComponents, true), (CurrentAssetsItems, false));
        var passed = Report((AssetsComponents, true), (CurrentAssetsItems, true));

        Assert.Equal(new ClaudeService.PassChoice(true, "integrity_passed"), ClaudeService.ChoosePass(failed, 0.69m, passed, 0.40m));
        Assert.Equal(new ClaudeService.PassChoice(false, "integrity_passed"), ClaudeService.ChoosePass(passed, 0.40m, failed, 0.69m));
    }

    [Fact]
    public void Fewer_failed_structure_checks_wins_when_both_fail()
    {
        var two = Report((CurrentAssetsItems, false), (CurrentLiabItems, false));
        var one = Report((CurrentAssetsItems, false), (CurrentLiabItems, true));

        Assert.Equal(new ClaudeService.PassChoice(true, "fewer_structure_failures"), ClaudeService.ChoosePass(two, 0.69m, one, 0.50m));
        Assert.Equal(new ClaudeService.PassChoice(false, "fewer_structure_failures"), ClaudeService.ChoosePass(one, 0.50m, two, 0.69m));
    }

    [Fact]
    public void Softer_checks_do_not_count_as_structure_failures()
    {
        // Both fail overall; the net-profit reconciliation is not a structure check, so it is a tie on
        // rule 2 and confidence decides.
        var structural = Report((CurrentAssetsItems, false), (NetProfit, true));
        var soft       = Report((CurrentAssetsItems, false), (NetProfit, false));

        Assert.Equal(new ClaudeService.PassChoice(false, "tie_first"), ClaudeService.ChoosePass(structural, 0.69m, soft, 0.69m));
    }

    [Fact]
    public void A_pass_that_skipped_the_check_the_other_failed_does_not_count_as_passed()
    {
        var failed  = Report((AssetsComponents, true), (CurrentAssetsItems, false));
        var skipped = Report((AssetsComponents, true)); // passes, but never evaluated the rubros

        // Rule 1 does not apply, rule 2 counts only checks both ran (0 vs 0), and the unverified pass
        // ranks with the capped confidence: 0.69 vs min(0.95, 0.69) → tie → first.
        Assert.Equal(new ClaudeService.PassChoice(false, "tie_first"), ClaudeService.ChoosePass(failed, 0.69m, skipped, 0.95m));
    }

    [Fact]
    public void Higher_confidence_decides_between_two_sound_passes_and_a_tie_keeps_the_first()
    {
        var sound = Report((AssetsComponents, true));

        Assert.Equal(new ClaudeService.PassChoice(true, "higher_confidence"), ClaudeService.ChoosePass(sound, 0.30m, sound, 0.90m));
        Assert.Equal(new ClaudeService.PassChoice(false, "higher_confidence"), ClaudeService.ChoosePass(sound, 0.90m, sound, 0.30m));
        Assert.Equal(new ClaudeService.PassChoice(false, "tie_first"), ClaudeService.ChoosePass(sound, 0.80m, sound, 0.80m));
    }

    [Fact]
    public void A_null_report_counts_as_not_passed()
    {
        var sound = Report((AssetsComponents, true));

        Assert.Equal(new ClaudeService.PassChoice(true, "integrity_passed"), ClaudeService.ChoosePass(null, 0.90m, sound, 0.50m));
        Assert.Equal(new ClaudeService.PassChoice(true, "higher_confidence"), ClaudeService.ChoosePass(null, 0.30m, null, 0.50m));
    }

    [Fact]
    public void Structure_checks_are_the_seven_totals_and_column_checks()
    {
        Assert.Equal(
            new[]
            {
                "accounting_equation", "assets_components", "column_swap", "current_assets_items",
                "current_liabilities_items", "liabilities_components", "non_current_liabilities_items",
            },
            BalanceIntegrityService.StructureChecks.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Failed_structure_checks_are_distinct_names_in_check_order()
    {
        var report = Report(
            (BalanceIntegrityService.ColumnSwapCheck, false),
            (CurrentAssetsItems, false),
            (BalanceIntegrityService.ColumnSwapCheck, false),
            (BalanceIntegrityService.SignAssetsCheck, false));

        Assert.Equal(["column_swap", "current_assets_items"], ClaudeService.FailedStructureChecks(report));
        Assert.Empty(ClaudeService.FailedStructureChecks(null));
    }

    // ── ExtractionIntegrityReader ─────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{ not json")]
    [InlineData("""{"assets": 1}""")]
    [InlineData("""{"integrity": {"checks": []}}""")]
    public void Reader_returns_null_without_a_usable_integrity_node(string? balanceJson)
    {
        Assert.Null(ExtractionIntegrityReader.Read(balanceJson));
    }

    // "integrity": null is what ClaudeService writes when the evaluation itself threw. It used to read
    // as null (indistinguishable from a pre-2.18 analysis); it now reads as an unverified verdict.
    [Fact]
    public void Reader_reports_an_explicit_null_integrity_node_as_unavailable_not_as_unknown()
    {
        var dto = ExtractionIntegrityReader.Read("""{"integrity": null}""");

        Assert.NotNull(dto);
        Assert.False(dto.Passed);
        Assert.False(dto.Verified);
        Assert.Equal([ExtractionIntegrityResponse.IntegrityUnavailableReason], dto.NotVerifiedReasons);
    }

    [Fact]
    public void Reader_leaves_the_second_pass_unknown_for_integrity_persisted_before_it_was_recorded()
    {
        var dto = ExtractionIntegrityReader.Read(
            """{"integrity": {"passed": true, "checks": [{"name": "assets_components", "passed": true}], "skipped": [], "affectedRatios": []}}""");

        Assert.NotNull(dto);
        Assert.True(dto.Passed);
        Assert.Empty(dto.FailedChecks);
        Assert.Empty(dto.AffectedRatios);
        Assert.Null(dto.SecondPassRan);
        Assert.Null(dto.KeptPass);
    }
}
