// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;
using Xunit;

namespace Api.Web.FinanceIA.Tests.BalanceCorrections;

/// <summary>
/// <see cref="BalanceCorrectionApplier"/> over the FINEX-shaped balance: the corrected figure, the
/// <c>manual_corrections</c> trail (first original kept), the re-evaluated integrity node (provenance kept,
/// <c>manuallyCorrected</c> added), and every other key untouched.
/// </summary>
public sealed class BalanceCorrectionApplierTests
{
    private static readonly Guid Analyst = Guid.Parse("3f2b7c1e-0000-4000-8000-000000000001");
    private static readonly DateTime At  = new(2026, 11, 2, 15, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void The_finex_misread_fails_current_assets_items_before_the_correction()
    {
        var integrity = Root(BalanceCorrectionFixtures.FinexBalanceJson())["integrity"]!;

        Assert.False(integrity["passed"]!.GetValue<bool>());
        Assert.Equal([BalanceIntegrityService.CurrentAssetsItemsCheck], FailedChecks(integrity));
    }

    [Fact]
    public void Correcting_the_misread_line_to_zero_makes_the_items_check_pass()
    {
        var applied = Apply(BalanceCorrectionFixtures.FinexBalanceJson(), BalanceCorrectionFixtures.FinexCommand());

        Assert.True(applied.IsApplied);
        Assert.True(applied.IntegrityPassed);

        var root      = Root(applied.BalanceJson!);
        var integrity = root["integrity"]!;
        Assert.True(integrity["passed"]!.GetValue<bool>());
        Assert.Empty(FailedChecks(integrity));
        Assert.Contains(integrity["checks"]!.AsArray(),
            c => c!["name"]!.GetValue<string>() == BalanceIntegrityService.CurrentAssetsItemsCheck && c["passed"]!.GetValue<bool>());
        Assert.Equal(0d, root["current_assets_items"]![1]!["amount"]!.GetValue<double>());

        var change = Assert.Single(applied.Applied);
        Assert.Equal(BalanceCorrectionFixtures.MisreadPath, change.Path);
        Assert.Equal(BalanceCorrectionFixtures.MisreadAmount, change.OldValue);
        Assert.Equal(0d, change.NewValue);
    }

    [Fact]
    public void The_integrity_node_keeps_the_reading_provenance_and_is_marked_manually_corrected()
    {
        var before  = Root(BalanceCorrectionFixtures.FinexBalanceJson())["integrity"]!;
        var applied = Apply(BalanceCorrectionFixtures.FinexBalanceJson(), BalanceCorrectionFixtures.FinexCommand());
        var after   = Root(applied.BalanceJson!)["integrity"]!;

        Assert.True(JsonNode.DeepEquals(before["extraction"], after["extraction"]));
        Assert.True(after["manuallyCorrected"]!.GetValue<bool>());
        Assert.Equal("miles", after["amountsUnit"]!.GetValue<string>());
        Assert.False(after["amountsUnitAssumed"]!.GetValue<bool>());

        // The reading's confidence was capped by the failed check; the corrected balance passes, so it is not.
        Assert.True(before["confidenceCapped"]!.GetValue<bool>());
        Assert.Equal(0.92, after["reportedConfidence"]!.GetValue<double>());
        Assert.Equal(0.92, after["effectiveConfidence"]!.GetValue<double>());
        Assert.False(after["confidenceCapped"]!.GetValue<bool>());
    }

    [Fact]
    public void A_correction_that_still_fails_a_check_keeps_the_confidence_capped()
    {
        var applied = Apply(BalanceCorrectionFixtures.FinexBalanceJson(), BalanceCorrectionFixtures.FinexCommand(value: 50_000_000d));
        var after   = Root(applied.BalanceJson!)["integrity"]!;

        Assert.False(applied.IntegrityPassed);
        Assert.Equal([BalanceIntegrityService.CurrentAssetsItemsCheck], FailedChecks(after));
        Assert.Equal(0.69, after["effectiveConfidence"]!.GetValue<double>());
        Assert.True(after["confidenceCapped"]!.GetValue<bool>());
    }

    [Fact]
    public void The_trail_records_path_original_corrected_by_at_and_reason()
    {
        var applied = Apply(BalanceCorrectionFixtures.FinexBalanceJson(), BalanceCorrectionFixtures.FinexCommand());

        var entry = Assert.Single(Root(applied.BalanceJson!)["manual_corrections"]!.AsArray())!;
        Assert.Equal(BalanceCorrectionFixtures.MisreadPath, entry["path"]!.GetValue<string>());
        Assert.Equal(BalanceCorrectionFixtures.MisreadAmount, entry["original"]!.GetValue<double>());
        Assert.Equal(0d, entry["corrected"]!.GetValue<double>());
        Assert.Equal(Analyst.ToString(), entry["by"]!.GetValue<string>());
        Assert.Equal(At, DateTime.Parse(entry["at"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind));
        Assert.Equal(BalanceCorrectionFixtures.Reason, entry["reason"]!.GetValue<string>());
    }

    [Fact]
    public void A_second_correction_of_the_same_path_keeps_the_first_original()
    {
        var first  = Apply(BalanceCorrectionFixtures.FinexBalanceJson(), BalanceCorrectionFixtures.FinexCommand(value: 5d));
        var second = Apply(first.BalanceJson!, BalanceCorrectionFixtures.FinexCommand(value: 0d));

        // The audit row of the second correction says what it overwrote…
        Assert.Equal(5d, Assert.Single(second.Applied).OldValue);

        // …and the trail keeps what the reading said.
        var trail = Root(second.BalanceJson!)["manual_corrections"]!.AsArray();
        Assert.Equal(2, trail.Count);
        Assert.All(trail, e => Assert.Equal(BalanceCorrectionFixtures.MisreadAmount, e!["original"]!.GetValue<double>()));
        Assert.Equal(5d, trail[0]!["corrected"]!.GetValue<double>());
        Assert.Equal(0d, trail[1]!["corrected"]!.GetValue<double>());
    }

    [Fact]
    public void A_figure_that_was_never_extracted_keeps_a_null_first_original()
    {
        var first  = Apply(BalanceCorrectionFixtures.FinexBalanceJson(), BalanceCorrectionFixtures.Command("prepaid_expenses", 10d));
        var second = Apply(first.BalanceJson!, BalanceCorrectionFixtures.Command("prepaid_expenses", 20d));

        var trail = Root(second.BalanceJson!)["manual_corrections"]!.AsArray();
        Assert.All(trail, e => Assert.Null(e!["original"]));
        Assert.True(trail[1]!.AsObject().ContainsKey("original"));
    }

    [Fact]
    public void Every_key_the_correction_does_not_own_is_unchanged()
    {
        var before = Root(BalanceCorrectionFixtures.FinexBalanceJson());
        var after  = Root(Apply(BalanceCorrectionFixtures.FinexBalanceJson(), BalanceCorrectionFixtures.Command("equity", 299_000_000d)).BalanceJson!);

        foreach (var (key, value) in before)
        {
            if (key is "equity" or "integrity")
                continue;
            Assert.True(JsonNode.DeepEquals(value, after[key]), $"{key} changed");
        }

        Assert.Equal(299_000_000d, after["equity"]!.GetValue<double>());
        Assert.Equal(before.Count + 1, after.Count);   // + manual_corrections
    }

    [Fact]
    public void A_top_level_correction_reruns_the_totals_checks()
    {
        // Equity off by 50M (beyond the checks' 1% rounding tolerance) breaks the accounting equation,
        // which the reading passed.
        var applied = Apply(BalanceCorrectionFixtures.CorrectedFigures(), BalanceCorrectionFixtures.Command("equity", 250_000_000d));

        Assert.False(applied.IntegrityPassed);
        Assert.Contains(BalanceIntegrityService.AccountingEquationCheck, FailedChecks(Root(applied.BalanceJson!)["integrity"]!));
    }

    // ── net_profit_reconciliation inputs (result_before_tax, income_tax) ────────

    [Fact]
    public void A_misread_result_before_tax_fails_only_net_profit_reconciliation()
    {
        var integrity = Root(BalanceCorrectionFixtures.MisreadResultBeforeTaxJson())["integrity"]!;

        Assert.False(integrity["passed"]!.GetValue<bool>());
        Assert.Equal([BalanceIntegrityService.NetProfitReconciliationCheck], FailedChecks(integrity));
    }

    [Fact]
    public void Correcting_result_before_tax_makes_net_profit_reconciliation_pass()
    {
        var applied = Apply(BalanceCorrectionFixtures.MisreadResultBeforeTaxJson(),
            BalanceCorrectionFixtures.Command("result_before_tax", BalanceCorrectionFixtures.ResultBeforeTax));

        Assert.True(applied.IsApplied);
        Assert.True(applied.IntegrityPassed);

        var root      = Root(applied.BalanceJson!);
        var integrity = root["integrity"]!;
        Assert.True(integrity["passed"]!.GetValue<bool>());
        Assert.Contains(integrity["checks"]!.AsArray(),
            c => c!["name"]!.GetValue<string>() == BalanceIntegrityService.NetProfitReconciliationCheck && c["passed"]!.GetValue<bool>());
        Assert.True(integrity["manuallyCorrected"]!.GetValue<bool>());
        Assert.Equal(BalanceCorrectionFixtures.ResultBeforeTax, root["result_before_tax"]!.GetValue<double>());

        var entry = Assert.Single(root["manual_corrections"]!.AsArray())!;
        Assert.Equal("result_before_tax", entry["path"]!.GetValue<string>());
        Assert.Equal(BalanceCorrectionFixtures.MisreadResultBeforeTax, entry["original"]!.GetValue<double>());
        Assert.Equal(BalanceCorrectionFixtures.ResultBeforeTax, entry["corrected"]!.GetValue<double>());
    }

    [Fact]
    public void A_corrected_income_tax_reaches_net_profit_reconciliation()
    {
        // 90M before tax and an 80M charge cannot net to a 60M profit under either tax reading.
        var applied = Apply(BalanceCorrectionFixtures.CorrectedFigures(),
            BalanceCorrectionFixtures.Command("income_tax", -80_000_000d));

        Assert.False(applied.IntegrityPassed);
        Assert.Equal([BalanceIntegrityService.NetProfitReconciliationCheck],
            FailedChecks(Root(applied.BalanceJson!)["integrity"]!));
    }

    [Fact]
    public void Clearing_result_before_tax_skips_net_profit_reconciliation()
    {
        var applied = Apply(BalanceCorrectionFixtures.MisreadResultBeforeTaxJson(),
            BalanceCorrectionFixtures.Command("result_before_tax", null));

        var integrity = Root(applied.BalanceJson!)["integrity"]!;
        Assert.True(applied.IntegrityPassed);
        Assert.Contains(BalanceIntegrityService.NetProfitReconciliationCheck,
            integrity["skipped"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void A_correction_the_balance_fails_returns_the_errors_and_no_json()
    {
        var applied = Apply(BalanceCorrectionFixtures.FinexBalanceJson(),
            BalanceCorrectionFixtures.Command("current_assets_items[9].amount", 1d));

        Assert.False(applied.IsApplied);
        Assert.Null(applied.BalanceJson);
        Assert.Equal("corrections[0].path", Assert.Single(applied.Errors).Field);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("{not json")]
    public void An_unreadable_balance_is_reported_as_such(string? balanceJson)
    {
        var applied = Apply(balanceJson, BalanceCorrectionFixtures.FinexCommand());

        Assert.True(applied.Unreadable);
        Assert.False(applied.IsApplied);
    }

    [Fact]
    public void A_balance_without_an_integrity_node_gains_one()
    {
        var root = Root(BalanceCorrectionFixtures.FinexBalanceJson());
        root.Remove("integrity");

        var after = Root(Apply(root.ToJsonString(), BalanceCorrectionFixtures.FinexCommand()).BalanceJson!)["integrity"]!;

        Assert.True(after["passed"]!.GetValue<bool>());
        Assert.True(after["manuallyCorrected"]!.GetValue<bool>());
        Assert.Equal("miles", after["amountsUnit"]!.GetValue<string>());
        Assert.False(after.AsObject().ContainsKey("extraction"));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static BalanceCorrectionApplication Apply(string? balanceJson, BalanceCorrectionCommand command)
        => BalanceCorrectionApplier.Apply(balanceJson, command, Analyst, At, new BalanceIntegrityService());

    private static JsonObject Root(string json) => JsonNode.Parse(json)!.AsObject();

    private static List<string> FailedChecks(JsonNode integrity)
        => integrity["checks"]!.AsArray()
            .Where(c => !c!["passed"]!.GetValue<bool>())
            .Select(c => c!["name"]!.GetValue<string>())
            .Distinct()
            .ToList();
}
