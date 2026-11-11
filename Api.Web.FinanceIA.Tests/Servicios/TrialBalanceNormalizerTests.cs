// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-04

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// <see cref="TrialBalanceNormalizer"/> over a SYNTHETIC pre-closing trial balance with the shape of a
/// "Balance de Presentación" printed from an accounting system: credit balances negative, equity without the
/// year's result, results accounts listed after equity. No figure comes from a real document:
/// Activo 1.000.000 = Pasivo 400.000 + PN impreso 450.000 + resultado 150.000, where the results accounts
/// (ventas −850.000, costo 500.000, administración 120.000, intereses 40.000, impuesto 60.000, RECPAM −20.000)
/// add up to −150.000 as printed.
/// </summary>
public sealed class TrialBalanceNormalizerTests
{
    // The results accounts as the ledger prints them (credits negative).
    private static readonly TrialBalanceAccount[] Results =
    [
        new(-850_000d, TrialBalanceNormalizer.Revenue),
        new(500_000d, TrialBalanceNormalizer.CostOfSales),
        new(120_000d, TrialBalanceNormalizer.OperatingExpense),
        new(40_000d, TrialBalanceNormalizer.Interest),
        new(60_000d, TrialBalanceNormalizer.IncomeTax),
        new(-20_000d, TrialBalanceNormalizer.Recpam),
    ];

    // What the extraction read: equity as printed (positive), the rubros with the ledger's credit sign against a
    // positive subtotal, and a net profit printed nowhere.
    private static ExtractedBalance ModelReading(
        double? liabilities = 400_000d, double? currentLiabilities = 300_000d, double? nonCurrentLiabilities = 100_000d,
        double? equity = 450_000d, double? assets = 1_000_000d) => new()
    {
        Current = new BalanceAccounts
        {
            TotalAssets           = assets,
            CurrentAssets         = 700_000d,
            NonCurrentAssets      = 300_000d,
            TotalLiabilities      = liabilities,
            CurrentLiabilities    = currentLiabilities,
            NonCurrentLiabilities = nonCurrentLiabilities,
            Equity                = equity,
            NetRevenue            = 850_000d,
            NetProfit             = 95_000d,
        },
        CurrentAssetsItems      = [Item("Caja y bancos", 200_000d), Item("Créditos por ventas", 500_000d)],
        CurrentLiabilitiesItems = [Item("Deudas comerciales", -180_000d), Item("Cargas fiscales", -120_000d)],
    };

    private static TrialBalanceExtraction Block(
        double equityPrinted = -450_000d, bool? creditNegative = true, IReadOnlyList<TrialBalanceAccount>? accounts = null) => new()
    {
        CreditBalancesNegative = creditNegative,
        EquityPrinted          = equityPrinted,
        ResultsAccounts        = accounts ?? Results,
    };

    private static BalanceLineItem Item(string label, double amount) => new() { Label = label, Amount = amount };

    // ── The pre-closing case ──────────────────────────────────────────────────

    [Fact]
    public void PreClosing_equity_includes_the_year_result_and_the_results_are_computed()
    {
        var outcome = TrialBalanceNormalizer.Normalize(ModelReading(), Block());
        var c       = outcome.Balance.Current;

        Assert.Equal(TrialBalanceNormalizer.ResultAdded, outcome.Audit.Adjustment);
        Assert.Equal(600_000d, c.Equity);
        Assert.Equal(150_000d, c.NetProfit);
        Assert.Equal(-60_000d, outcome.Balance.IncomeTax);
        Assert.Equal(210_000d, outcome.Balance.ResultBeforeTax);
        Assert.Equal(-40_000d, c.FinancialExpenses);
        Assert.Equal(230_000d, c.OperatingResult);
        Assert.Equal(850_000d, c.NetRevenue);

        // The audit keeps the printed equity and what the model had said.
        Assert.True(outcome.Audit.CreditBalancesNegative);
        Assert.Equal(450_000d, outcome.Audit.EquityPrinted);
        Assert.Equal(150_000d, outcome.Audit.YearResult);
        Assert.Equal(450_000d, outcome.Audit.Replaced!["equity"]);
        Assert.Equal(95_000d, outcome.Audit.Replaced["net_profit"]);
        Assert.Null(outcome.Audit.Replaced["operating_result"]);
        Assert.Equal(850_000d, outcome.Audit.ResultsByClass![TrialBalanceNormalizer.Revenue]);
    }

    [Fact]
    public void PreClosing_rubros_carrying_the_credit_sign_become_positive()
    {
        var outcome = TrialBalanceNormalizer.Normalize(ModelReading(), Block());

        Assert.Equal([180_000d, 120_000d], outcome.Balance.CurrentLiabilitiesItems!.Select(i => i.Amount!.Value));
        Assert.Equal(["Deudas comerciales", "Cargas fiscales"], outcome.Balance.CurrentLiabilitiesItems!.Select(i => i.Label));
        Assert.Equal(["current_liabilities_items"], outcome.Audit.SignsNormalized);

        // Assets are debit balances: never touched.
        Assert.Equal([200_000d, 500_000d], outcome.Balance.CurrentAssetsItems!.Select(i => i.Amount!.Value));
    }

    [Fact]
    public void PreClosing_negative_totals_become_magnitudes()
    {
        var outcome = TrialBalanceNormalizer.Normalize(
            ModelReading(liabilities: -400_000d, currentLiabilities: -300_000d, nonCurrentLiabilities: -100_000d), Block());
        var c = outcome.Balance.Current;

        Assert.Equal(400_000d, c.TotalLiabilities);
        Assert.Equal(300_000d, c.CurrentLiabilities);
        Assert.Equal(100_000d, c.NonCurrentLiabilities);
        Assert.Equal(
            ["liabilities", "current_liabilities", "non_current_liabilities", "current_liabilities_items"],
            outcome.Audit.SignsNormalized);
        Assert.Equal(600_000d, c.Equity);
    }

    [Fact]
    public void PreClosing_balance_passes_the_accounting_equation_and_the_items_check()
    {
        var before = new BalanceIntegrityService().Evaluate(ModelReading()).Report;
        Assert.Contains(before.Checks, ch => ch.Name == BalanceIntegrityService.AccountingEquationCheck && !ch.Passed);
        Assert.Contains(before.Checks, ch => ch.Name == BalanceIntegrityService.CurrentLiabilitiesItemsCheck && !ch.Passed);

        var after = new BalanceIntegrityService().Evaluate(TrialBalanceNormalizer.Normalize(ModelReading(), Block()).Balance).Report;

        Assert.True(after.Passed);
        Assert.Contains(after.Checks, ch => ch.Name == BalanceIntegrityService.AccountingEquationCheck && ch.Passed);
        Assert.Contains(after.Checks, ch => ch.Name == BalanceIntegrityService.CurrentLiabilitiesItemsCheck && ch.Passed);
        Assert.Contains(after.Checks, ch => ch.Name == BalanceIntegrityService.NetProfitReconciliationCheck && ch.Passed);
    }

    /// <summary>
    /// Credit-signed rubros under a NULL pasivo no corriente subtotal stay negative (the normalizer flips rubros
    /// against a printed positive subtotal only). liabilities_components must keep the skip rather than evaluate
    /// those rubros as a negative component and fail a correct reading.
    /// </summary>
    [Fact]
    public void PreClosing_credit_signed_rubros_under_a_null_subtotal_never_fail_liabilities_components()
    {
        var reading = ModelReading(nonCurrentLiabilities: null) with
        {
            NonCurrentLiabilitiesItems = [Item("Préstamos a largo plazo", -100_000d)],
        };

        var outcome = TrialBalanceNormalizer.Normalize(reading, Block());
        Assert.Equal(-100_000d, outcome.Balance.NonCurrentLiabilitiesItems![0].Amount);

        var report = new BalanceIntegrityService().Evaluate(outcome.Balance).Report;

        Assert.Contains(BalanceIntegrityService.LiabilitiesComponentsCheck, report.Skipped);
        Assert.DoesNotContain(report.Checks, ch => ch.Name == BalanceIntegrityService.LiabilitiesComponentsCheck);
        Assert.True(report.Passed);
    }

    [Fact]
    public void PreClosing_adds_one_anomaly_stating_the_adjustment_and_the_figure_added()
    {
        var outcome = TrialBalanceNormalizer.Normalize(ModelReading(), Block());

        var anomaly = Assert.Single(outcome.Anomalies);
        Assert.Equal(
            "El documento es un balance de sumas y saldos previo al cierre: el patrimonio neto impreso ($450.000) no " +
            "incluye el resultado del ejercicio. Se le sumó el resultado del ejercicio calculado a partir de las cuentas " +
            "de resultado ($150.000), con lo que el patrimonio neto asciende a $600.000.",
            anomaly);
        Assert.DoesNotContain("%", anomaly);
    }

    [Fact]
    public void A_balance_printed_in_thousands_is_quoted_in_thousands()
    {
        var outcome = TrialBalanceNormalizer.Normalize(ModelReading().Scale(1_000d), Block().Scale(1_000d));

        Assert.Equal(600_000_000d, outcome.Balance.Current.Equity);
        Assert.Contains("($450.000, en miles)", Assert.Single(outcome.Anomalies));
    }

    // ── Other readings ────────────────────────────────────────────────────────

    [Fact]
    public void Equity_that_already_includes_the_result_is_kept_and_the_results_still_computed()
    {
        var outcome = TrialBalanceNormalizer.Normalize(ModelReading(equity: 600_000d), Block(equityPrinted: -600_000d));

        Assert.Equal(TrialBalanceNormalizer.AlreadyIncluded, outcome.Audit.Adjustment);
        Assert.Equal(600_000d, outcome.Balance.Current.Equity);
        Assert.Equal(150_000d, outcome.Balance.Current.NetProfit);
        Assert.False(outcome.Audit.Replaced!.ContainsKey("equity"));
        Assert.Empty(outcome.Anomalies);
    }

    [Fact]
    public void A_ledger_printing_credits_positive_is_resolved_by_the_equation()
    {
        var flipped = Results.Select(a => a with { Amount = -a.Amount }).ToList();

        var outcome = TrialBalanceNormalizer.Normalize(ModelReading(), Block(equityPrinted: 450_000d, creditNegative: null, accounts: flipped));

        Assert.Equal(TrialBalanceNormalizer.ResultAdded, outcome.Audit.Adjustment);
        Assert.False(outcome.Audit.CreditBalancesNegative);
        Assert.Equal(600_000d, outcome.Balance.Current.Equity);
        Assert.Equal(150_000d, outcome.Balance.Current.NetProfit);
        Assert.Equal(-60_000d, outcome.Balance.IncomeTax);
    }

    [Fact]
    public void A_group_listed_with_its_own_accounts_does_not_reconcile_and_changes_nothing()
    {
        TrialBalanceAccount[] doubled = [new(-850_000d, TrialBalanceNormalizer.Revenue), .. Results];
        var reading = ModelReading();

        var outcome = TrialBalanceNormalizer.Normalize(reading, Block(accounts: doubled));

        Assert.Equal(TrialBalanceNormalizer.NotReconciled, outcome.Audit.Adjustment);
        Assert.Equal(450_000d, outcome.Balance.Current.Equity);
        Assert.Equal(95_000d, outcome.Balance.Current.NetProfit);
        Assert.Null(outcome.Balance.ResultBeforeTax);
        Assert.Null(outcome.Audit.YearResult);
        Assert.Empty(outcome.Anomalies);
        // The credit sign is still a reading fix, whatever the equation says.
        Assert.Equal(["current_liabilities_items"], outcome.Audit.SignsNormalized);
    }

    [Fact]
    public void An_unclassified_account_leaves_the_operating_result_alone()
    {
        TrialBalanceAccount[] accounts = [.. Results.Take(5), new(-20_000d, "misc")];

        var outcome = TrialBalanceNormalizer.Normalize(ModelReading(), Block(accounts: accounts));

        Assert.Equal(TrialBalanceNormalizer.ResultAdded, outcome.Audit.Adjustment);
        Assert.Null(outcome.Balance.Current.OperatingResult);
        Assert.Equal(150_000d, outcome.Balance.Current.NetProfit);
    }

    [Fact]
    public void Without_an_income_tax_account_the_pre_tax_figures_stay_as_read()
    {
        TrialBalanceAccount[] accounts = [.. Results.Where(a => a.Class != TrialBalanceNormalizer.IncomeTax), new(60_000d, TrialBalanceNormalizer.Other)];

        var outcome = TrialBalanceNormalizer.Normalize(ModelReading(), Block(accounts: accounts));

        Assert.Equal(150_000d, outcome.Balance.Current.NetProfit);
        Assert.Null(outcome.Balance.IncomeTax);
        Assert.Null(outcome.Balance.ResultBeforeTax);
    }

    [Fact]
    public void An_unreadable_account_amount_makes_the_result_unknown()
    {
        TrialBalanceAccount[] accounts = [.. Results, new(null, TrialBalanceNormalizer.Other)];

        var outcome = TrialBalanceNormalizer.Normalize(ModelReading(), Block(accounts: accounts));

        Assert.Equal(TrialBalanceNormalizer.NotReconciled, outcome.Audit.Adjustment);
        Assert.Equal(95_000d, outcome.Balance.Current.NetProfit);
    }

    [Fact]
    public void Without_assets_nothing_is_computable()
    {
        var outcome = TrialBalanceNormalizer.Normalize(ModelReading(assets: null), Block());

        Assert.Equal(TrialBalanceNormalizer.NotComputable, outcome.Audit.Adjustment);
        Assert.Equal(450_000d, outcome.Balance.Current.Equity);
    }

    [Fact]
    public void Without_a_block_nothing_is_computable_and_the_equity_is_left_alone()
    {
        var outcome = TrialBalanceNormalizer.Normalize(ModelReading(), null);

        Assert.Equal(TrialBalanceNormalizer.NotComputable, outcome.Audit.Adjustment);
        Assert.Equal(450_000d, outcome.Balance.Current.Equity);
    }

    [Theory]
    [InlineData("trial_balance", true)]
    [InlineData(" TRIAL_BALANCE ", true)]
    [InlineData("financial_statements", false)]
    [InlineData(null, false)]
    public void IsTrialBalance_reads_the_document_kind(string? kind, bool expected)
        => Assert.Equal(expected, TrialBalanceNormalizer.IsTrialBalance(kind));
}
