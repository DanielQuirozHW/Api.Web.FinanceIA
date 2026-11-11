// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-24

using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Pins the deterministic balance integrity checks (M11). The anchor is a real column-swap case: a
/// scanned statement whose pasivo corriente was read from the prior year's "Total del pasivo y
/// patrimonio neto" column, and whose pasivo no corriente was derived as a difference — two wrong
/// figures that still add up to the right total pasivo, so P = PC + PNC passes. Only the sum of the
/// pasivo corriente rubros and the comparison against the prior-year column catch it.
/// </summary>
public sealed class BalanceIntegrityServiceTests
{
    private readonly BalanceIntegrityService _sut = new();

    // ── Fixture: the pinned column-swap case ──────────────────────────────────
    // Liability figures and the pasivo corriente rubros are the real ones. The activo corriente,
    // bienes de cambio, activo no corriente and the whole prior-year column (except its total,
    // which is what the swapped figure matched) are SYNTHETIC, chosen so the ratios reproduce the
    // observed 1,51 / 0,94 (wrong column) versus 1,67 / 1,04 (correct column).

    private const double WrongCurrentLiabilities    = 47_637_949_041d;
    private const double WrongNonCurrentLiabilities = 7_935_166_515d;
    private const double RealCurrentLiabilities     = 43_247_190_286d;
    private const double RealNonCurrentLiabilities  = 12_325_925_270d;
    private const double TotalLiabilities           = 55_573_115_556d;
    private const double PriorYearTotalAssets       = 47_637_949_041d; // "Total del pasivo y PN" of the prior year

    private const double CurrentAssets    = 72_100_000_000d; // synthetic
    private const double Inventory        = 27_200_000_000d; // synthetic
    private const double NonCurrentAssets = 15_000_000_000d; // synthetic
    private const double TotalAssets      = CurrentAssets + NonCurrentAssets;

    private static IReadOnlyList<BalanceLineItem> CurrentLiabilityRubros() =>
    [
        new() { Label = "Deudas comerciales",               Amount = 33_190_632_263d },
        new() { Label = "Préstamos",                        Amount = 4_997_185_813d },
        new() { Label = "Remuneraciones y cargas sociales", Amount = 526_531_729d },
        new() { Label = "Cargas fiscales",                  Amount = 4_532_840_481d },
        new() { Label = "Otros pasivos",                    Amount = 0d },
    ];

    private static BalanceAccounts ColumnSwapCurrentYear(
        double currentLiabilities = WrongCurrentLiabilities,
        double nonCurrentLiabilities = WrongNonCurrentLiabilities) => new()
    {
        TotalAssets           = TotalAssets,
        CurrentAssets         = CurrentAssets,
        NonCurrentAssets      = NonCurrentAssets,
        TotalLiabilities      = TotalLiabilities,
        CurrentLiabilities    = currentLiabilities,
        NonCurrentLiabilities = nonCurrentLiabilities,
        Equity                = TotalAssets - TotalLiabilities,
        Inventory             = Inventory,
    };

    private static BalanceAccounts ColumnSwapPriorYear() => new()
    {
        TotalAssets           = PriorYearTotalAssets,
        CurrentAssets         = 35_000_000_000d,
        NonCurrentAssets      = 12_637_949_041d,
        TotalLiabilities      = 30_000_000_000d,
        CurrentLiabilities    = 25_000_000_000d,
        NonCurrentLiabilities = 5_000_000_000d,
        Equity                = 17_637_949_041d,
    };

    private static ExtractedBalance ColumnSwapCase() => new()
    {
        Current                 = ColumnSwapCurrentYear(),
        PriorYear               = ColumnSwapPriorYear(),
        CurrentLiabilitiesItems = CurrentLiabilityRubros(),
    };

    private static BalanceIntegrityCheck Check(BalanceIntegrityReport report, string name) =>
        Assert.Single(report.Checks, c => c.Name == name);

    // ── Pinned column-swap case ───────────────────────────────────────────────

    [Fact]
    public void ColumnSwapCase_RubrosCheckCatchesTheCurrentLiabilities()
    {
        var outcome = _sut.Evaluate(ColumnSwapCase());

        Assert.False(outcome.Report.Passed);

        // The two wrong figures still add up to the right total, so this check cannot see it.
        Assert.True(Check(outcome.Report, BalanceIntegrityService.LiabilitiesComponentsCheck).Passed);

        var rubros = Check(outcome.Report, BalanceIntegrityService.CurrentLiabilitiesItemsCheck);
        Assert.False(rubros.Passed);
        Assert.Equal(RealCurrentLiabilities, rubros.Expected);
        Assert.Equal(WrongCurrentLiabilities, rubros.Actual);
        Assert.Equal(9.22, rubros.DiffPct);

        Assert.Contains(
            "La suma de los rubros del pasivo corriente ($43.247.190.286) no coincide con el pasivo " +
            "corriente informado ($47.637.949.041).",
            outcome.Anomalies);
    }

    [Fact]
    public void ColumnSwapCase_DetectsTheFigureReadFromTheComparativeColumn()
    {
        var outcome = _sut.Evaluate(ColumnSwapCase());

        var swap = Check(outcome.Report, BalanceIntegrityService.ColumnSwapCheck);
        Assert.False(swap.Passed);
        Assert.Equal(["current_liabilities"], swap.Lines);
        Assert.Equal("prior_year.assets", swap.ComparedWith);
        Assert.Equal(PriorYearTotalAssets, swap.Expected);

        Assert.Contains(
            "El pasivo corriente informado ($47.637.949.041) coincide con el activo total del ejercicio " +
            "anterior; es posible que se haya leído la columna comparativa.",
            outcome.Anomalies);
    }

    [Fact]
    public void ColumnSwapCase_AffectedRatiosAreLiquidezAndPruebaAcida()
    {
        var outcome = _sut.Evaluate(ColumnSwapCase());

        Assert.Equal(["liquidez_corriente", "prueba_acida"], outcome.Report.AffectedRatios);
    }

    [Fact]
    public void ColumnSwapCase_TheFlaggedLineIsWhatMovesTheRatios()
    {
        var ratios = new RatioCalculationService();

        var wrong = ratios.Calculate(ColumnSwapCurrentYear());
        var real  = ratios.Calculate(ColumnSwapCurrentYear(RealCurrentLiabilities, RealNonCurrentLiabilities));

        Assert.Equal(1.51, wrong.CurrentRatio);
        Assert.Equal(0.94, wrong.AcidTest);
        Assert.Equal(1.67, real.CurrentRatio);
        Assert.Equal(1.04, real.AcidTest);
    }

    [Fact]
    public void ColumnSwapCase_CorrectFiguresPassEveryCheck()
    {
        var balance = ColumnSwapCase() with
        {
            Current = ColumnSwapCurrentYear(RealCurrentLiabilities, RealNonCurrentLiabilities)
        };

        var outcome = _sut.Evaluate(balance);

        Assert.True(outcome.Report.Passed);
        Assert.Empty(outcome.Anomalies);
        Assert.Empty(outcome.Report.AffectedRatios);
        Assert.True(Check(outcome.Report, BalanceIntegrityService.ColumnSwapCheck).Passed);
    }

    [Fact]
    public void ColumnSwapCase_InThousands_QuotesTheAnomalyInPrintedUnits()
    {
        // The same statement "expresado en miles": the model reports 47.637.949 and the pipeline
        // scales it to pesos before the checks. The anomaly must quote the figure the analyst reads
        // on the document and in the model's own anomalies, not the peso figure, and say so.
        var printed = new ExtractedBalance
        {
            Current = new BalanceAccounts
            {
                CurrentLiabilities = 47_637_949d, TotalLiabilities = 55_573_116d, NonCurrentLiabilities = 7_935_167d,
            },
            PriorYear = new BalanceAccounts { TotalAssets = 47_637_949d },
            CurrentLiabilitiesItems =
            [
                new() { Label = "Deudas comerciales",               Amount = 33_190_632d },
                new() { Label = "Préstamos",                        Amount = 4_997_186d },
                new() { Label = "Remuneraciones y cargas sociales", Amount = 526_532d },
                new() { Label = "Cargas fiscales",                  Amount = 4_532_840d },
            ],
        };

        var outcome = _sut.Evaluate(printed.Scale(1_000d));

        Assert.Contains(
            "La suma de los rubros del pasivo corriente ($43.247.190, en miles) no coincide con el pasivo " +
            "corriente informado ($47.637.949, en miles).",
            outcome.Anomalies);
        Assert.Contains(
            "El pasivo corriente informado ($47.637.949, en miles) coincide con el activo total del ejercicio " +
            "anterior; es posible que se haya leído la columna comparativa.",
            outcome.Anomalies);
        Assert.All(outcome.Anomalies, a => Assert.DoesNotContain("%", a));

        // The persisted report stays in pesos, like every other amount in BalanceJson.
        var rubros = Check(outcome.Report, BalanceIntegrityService.CurrentLiabilitiesItemsCheck);
        Assert.Equal(43_247_190_000d, rubros.Expected);
        Assert.Equal(47_637_949_000d, rubros.Actual);
    }

    [Fact]
    public void Millions_QuoteTheAnomalyInMillions()
    {
        var printed = new ExtractedBalance
        {
            Current = new BalanceAccounts { TotalAssets = 100d, TotalLiabilities = 60d, Equity = 30d }
        };

        var outcome = _sut.Evaluate(printed.Scale(1_000_000d));

        Assert.Contains(
            "El activo total informado ($100, en millones) no coincide con la suma del pasivo total y el " +
            "patrimonio neto ($90, en millones).",
            outcome.Anomalies);
    }

    // ── Accounting identities ─────────────────────────────────────────────────

    [Fact]
    public void NegativeEquity_AccountingEquationPassesWithTheNegativeEquity()
    {
        var balance = new ExtractedBalance
        {
            Current = new BalanceAccounts
            {
                TotalAssets = 100_000d, CurrentAssets = 60_000d, NonCurrentAssets = 40_000d,
                TotalLiabilities = 150_000d, CurrentLiabilities = 90_000d, NonCurrentLiabilities = 60_000d,
                Equity = -50_000d,
            }
        };

        var outcome = _sut.Evaluate(balance);

        var equation = Check(outcome.Report, BalanceIntegrityService.AccountingEquationCheck);
        Assert.True(equation.Passed);
        Assert.Equal(100_000d, equation.Expected);
        Assert.True(outcome.Report.Passed);
    }

    [Fact]
    public void AccountingEquation_Mismatch_FlagsAssetsLiabilitiesAndEquityRatios()
    {
        var balance = new ExtractedBalance
        {
            Current = new BalanceAccounts { TotalAssets = 100_000d, TotalLiabilities = 60_000d, Equity = 30_000d }
        };

        var outcome = _sut.Evaluate(balance);

        Assert.False(Check(outcome.Report, BalanceIntegrityService.AccountingEquationCheck).Passed);
        Assert.Equal(["endeudamiento", "leverage", "roe", "roa"], outcome.Report.AffectedRatios);
        Assert.Contains(
            "El activo total informado ($100.000) no coincide con la suma del pasivo total y el patrimonio neto ($90.000).",
            outcome.Anomalies);
    }

    [Theory]
    [InlineData(100_900d, true)]  // 0,89 % off — rounding in a printed statement
    [InlineData(101_200d, false)] // 1,19 % off
    public void Tolerance_IsOnePercentOfTheLargerMagnitude(double assets, bool passes)
    {
        var balance = new ExtractedBalance
        {
            Current = new BalanceAccounts { TotalAssets = assets, CurrentAssets = 60_000d, NonCurrentAssets = 40_000d }
        };

        var outcome = _sut.Evaluate(balance);

        Assert.Equal(passes, Check(outcome.Report, BalanceIntegrityService.AssetsComponentsCheck).Passed);
    }

    [Fact]
    public void Rubros_WithAnIllegibleLine_SkipTheCheckRatherThanSumAPartialList()
    {
        var balance = new ExtractedBalance
        {
            Current = new BalanceAccounts { CurrentAssets = 1_000d },
            CurrentAssetsItems = [new() { Label = "Caja", Amount = 400d }, new() { Label = "Créditos", Amount = null }],
        };

        var outcome = _sut.Evaluate(balance);

        Assert.Contains(BalanceIntegrityService.CurrentAssetsItemsCheck, outcome.Report.Skipped);
        Assert.DoesNotContain(outcome.Report.Checks, c => c.Name == BalanceIntegrityService.CurrentAssetsItemsCheck);
    }

    [Fact]
    public void Rubros_SumSignedAmounts()
    {
        var balance = new ExtractedBalance
        {
            Current = new BalanceAccounts { CurrentAssets = 900d },
            CurrentAssetsItems =
            [
                new() { Label = "Créditos por ventas", Amount = 1_000d },
                new() { Label = "Previsión para incobrables", Amount = -100d },
            ],
        };

        var outcome = _sut.Evaluate(balance);

        Assert.True(Check(outcome.Report, BalanceIntegrityService.CurrentAssetsItemsCheck).Passed);
    }

    // ── Net profit ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1_000d, -300d, 700d,   true)]  // tax signed as a charge
    [InlineData(1_000d, 300d,  700d,   true)]  // tax reported as a magnitude
    [InlineData(1_000d, 200d,  1_200d, true)]  // tax benefit
    [InlineData(-500d,  -50d,  -550d,  true)]  // a loss with a tax charge
    [InlineData(1_000d, -300d, 900d,   false)]
    public void NetProfit_ReconcilesWithResultBeforeTaxAndIncomeTax(
        double resultBeforeTax, double incomeTax, double netProfit, bool passes)
    {
        var balance = new ExtractedBalance
        {
            Current         = new BalanceAccounts { NetProfit = netProfit },
            ResultBeforeTax = resultBeforeTax,
            IncomeTax       = incomeTax,
        };

        var outcome = _sut.Evaluate(balance);

        Assert.Equal(passes, Check(outcome.Report, BalanceIntegrityService.NetProfitReconciliationCheck).Passed);
        // None of these is a sign flip: the record stays off the check.
        Assert.Null(Check(outcome.Report, BalanceIntegrityService.NetProfitReconciliationCheck).SignFlip);
        if (!passes)
            Assert.Equal(["roe", "roa", "margen_neto"], outcome.Report.AffectedRatios);
    }

    // ── Net profit read with the wrong sign (reliability policy 5 guard) ─────────

    [Fact]
    public void NetProfit_ReadWithTheWrongSign_FailsAndRecordsTheSignFlip()
    {
        // Result before tax −12.000, a tax benefit of 2.000: a loss of 10.000, reported as a profit of 10.000.
        var balance = new ExtractedBalance
        {
            Current         = new BalanceAccounts { NetProfit = 10_000d },
            ResultBeforeTax = -12_000d,
            IncomeTax       = 2_000d,
        };

        var outcome = _sut.Evaluate(balance);
        var check   = Check(outcome.Report, BalanceIntegrityService.NetProfitReconciliationCheck);

        Assert.False(check.Passed);
        // The stored expected is the reading nearer in RELATIVE terms (magnitude, −14.000); the flip is judged on
        // both readings, and the signed one (−10.000) is exactly the negative of the reported 10.000.
        Assert.Equal(-14_000d, check.Expected);
        Assert.True(check.SignFlip);
        Assert.True(BalanceIntegrityService.NetProfitSignFlipOf(outcome.Report.Checks));
        Assert.False(BalanceIntegrityService.FailsOnlyOnPresentation(["net_profit_reconciliation"], true, null));

        var node = JsonSerializer.SerializeToNode(check)!.AsObject();
        Assert.True(node["signFlip"]!.GetValue<bool>());
    }

    [Fact]
    public void NetProfit_PresentationGap_FailsWithoutASignFlip()
    {
        // 15.193.935 − 5.317.877 = 9.876.058 expected; the statement reports 12.280.839 (financial results left out).
        var balance = new ExtractedBalance
        {
            Current         = new BalanceAccounts { NetProfit = 12_280_839d },
            ResultBeforeTax = 15_193_935d,
            IncomeTax       = -5_317_877d,
        };

        var outcome = _sut.Evaluate(balance);
        var check   = Check(outcome.Report, BalanceIntegrityService.NetProfitReconciliationCheck);

        Assert.False(check.Passed);
        Assert.Equal(9_876_058d, check.Expected);
        Assert.Null(check.SignFlip);
        Assert.False(BalanceIntegrityService.NetProfitSignFlipOf(outcome.Report.Checks));
        Assert.True(BalanceIntegrityService.FailsOnlyOnPresentation(["net_profit_reconciliation"], false, null));

        var node = JsonSerializer.SerializeToNode(check)!.AsObject();
        Assert.False(node.ContainsKey("signFlip"));
    }

    [Theory]
    [InlineData(10_000d,  -12_000d, 2_000d,    true)]   // the signed reading, negated
    [InlineData(14_000d,  -12_000d, 2_000d,    true)]   // the magnitude reading (−12.000 − 2.000), negated
    [InlineData(10_050d,  -12_000d, 2_000d,    true)]   // within the 1 % tolerance
    [InlineData(10_200d,  -12_000d, 2_000d,    false)]  // outside it
    [InlineData(-700d,    1_000d,   -300d,     true)]   // a profit read as a loss
    [InlineData(12_280_839d, 15_193_935d, -5_317_877d, false)]
    public void IsNetProfitSignFlip_TestsBothReadingsNegated(double netProfit, double resultBeforeTax, double incomeTax, bool expected)
        => Assert.Equal(expected, BalanceIntegrityService.IsNetProfitSignFlip(netProfit, resultBeforeTax, incomeTax));

    [Fact]
    public void NetProfitSignFlipOf_IsNullWhenNetProfitDidNotFail()
        => Assert.Null(BalanceIntegrityService.NetProfitSignFlipOf(
            [new BalanceIntegrityCheck { Name = BalanceIntegrityService.NetProfitReconciliationCheck, Passed = true }]));

    // ── net_profit_reconciliation: column mix (policy 6) ─────────────────────
    // Synthetic two-column income statement, both columns reconciling as printed:
    //   current  — result before tax 1.000.000, tax −350.000, net profit 650.000;
    //   previous — result before tax   800.000, tax −280.000, net profit 520.000.

    private static ExtractedBalance IncomeStatement(
        double netProfit, double resultBeforeTax, double incomeTax, double? priorNetProfit, bool hasPriorColumn = true) => new()
    {
        Current         = new BalanceAccounts { NetProfit = netProfit },
        ResultBeforeTax = resultBeforeTax,
        IncomeTax       = incomeTax,
        PriorYear       = hasPriorColumn ? new BalanceAccounts { NetProfit = priorNetProfit } : null,
    };

    [Theory]
    [InlineData(-280_000d)]   // the tax signed as printed: 800.000 − 280.000
    [InlineData(280_000d)]    // the tax as a magnitude:   800.000 − |280.000|
    public void NetProfit_PreTaxAndTaxFromTheComparativeColumn_IsAColumnMix(double priorTaxAsRead)
    {
        var outcome = _sut.Evaluate(IncomeStatement(650_000d, 800_000d, priorTaxAsRead, priorNetProfit: 520_000d));
        var check   = Check(outcome.Report, BalanceIntegrityService.NetProfitReconciliationCheck);

        Assert.False(check.Passed);
        Assert.True(check.ColumnMix);
        Assert.Null(check.SignFlip);
        Assert.True(BalanceIntegrityService.NetProfitColumnMixOf(outcome.Report.Checks));
        Assert.False(BalanceIntegrityService.NetProfitSignFlipOf(outcome.Report.Checks));
        Assert.False(BalanceIntegrityService.FailsOnlyOnPresentation(
            [BalanceIntegrityService.NetProfitReconciliationCheck], false, true));

        var node = JsonSerializer.SerializeToNode(check)!.AsObject();
        Assert.True(node["columnMix"]!.GetValue<bool>());
        Assert.False(node.ContainsKey("signFlip"));

        // The anomaly keeps its sentence and adds the column one; still no "%".
        var anomaly = Assert.Single(outcome.Anomalies);
        Assert.EndsWith(" " + BalanceIntegrityService.ColumnMixAnomalySentence, anomaly, StringComparison.Ordinal);
        Assert.StartsWith("El resultado neto informado", anomaly, StringComparison.Ordinal);
        Assert.DoesNotContain("%", anomaly, StringComparison.Ordinal);
    }

    [Fact]
    public void NetProfit_PresentationWithAComparativeThatDoesNotMatch_IsCheckedAndNotAColumnMix()
    {
        // RECPAM after the tax: the printed net profit (600.000) is not 1.000.000 − 350.000, and the prior year's
        // 520.000 is not 650.000 either — a presentation, exactly as before.
        var outcome = _sut.Evaluate(IncomeStatement(600_000d, 1_000_000d, -350_000d, priorNetProfit: 520_000d));
        var check   = Check(outcome.Report, BalanceIntegrityService.NetProfitReconciliationCheck);

        Assert.False(check.Passed);
        Assert.False(check.ColumnMix);
        Assert.False(BalanceIntegrityService.NetProfitColumnMixOf(outcome.Report.Checks));
        Assert.True(BalanceIntegrityService.FailsOnlyOnPresentation(
            [BalanceIntegrityService.NetProfitReconciliationCheck], false, check.ColumnMix));

        var node = JsonSerializer.SerializeToNode(check)!.AsObject();
        Assert.False(node["columnMix"]!.GetValue<bool>());
        Assert.DoesNotContain(BalanceIntegrityService.ColumnMixAnomalySentence, Assert.Single(outcome.Anomalies), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, null)]   // first fiscal year: no comparative column at all
    [InlineData(true,  null)]   // a comparative column whose net profit is not legible
    [InlineData(true,  0d)]     // a zero is no evidence
    public void NetProfit_WithoutAUsableComparative_LeavesTheColumnMixUnknown(bool hasPriorColumn, double? priorNetProfit)
    {
        var outcome = _sut.Evaluate(IncomeStatement(650_000d, 800_000d, -280_000d, priorNetProfit, hasPriorColumn));
        var check   = Check(outcome.Report, BalanceIntegrityService.NetProfitReconciliationCheck);

        Assert.False(check.Passed);
        Assert.Null(check.ColumnMix);
        Assert.Null(BalanceIntegrityService.NetProfitColumnMixOf(outcome.Report.Checks));
        Assert.False(JsonSerializer.SerializeToNode(check)!.AsObject().ContainsKey("columnMix"));
        // Unknown keeps today's verdict: a presentation when the flip is known not to be one.
        Assert.True(BalanceIntegrityService.FailsOnlyOnPresentation(
            [BalanceIntegrityService.NetProfitReconciliationCheck], false, check.ColumnMix));
    }

    [Fact]
    public void NetProfit_ThatReconciles_RecordsNoColumnMix_EvenWhenTheComparativeMatches()
    {
        // A flat year: the current column reconciles; that the prior net profit is the same says nothing.
        var outcome = _sut.Evaluate(IncomeStatement(520_000d, 800_000d, -280_000d, priorNetProfit: 520_000d));
        var check   = Check(outcome.Report, BalanceIntegrityService.NetProfitReconciliationCheck);

        Assert.True(check.Passed);
        Assert.Null(check.ColumnMix);
        Assert.Null(BalanceIntegrityService.NetProfitColumnMixOf(outcome.Report.Checks));
    }

    [Fact]
    public void NetProfit_SignFlipAndColumnMix_AreBothRecorded_AndEitherIsAMisread()
    {
        // The pre-tax result and tax are last year's (800.000 − 280.000 = the prior 520.000) AND the net profit was
        // read as −520.000: both verdicts hold.
        var outcome = _sut.Evaluate(IncomeStatement(-520_000d, 800_000d, -280_000d, priorNetProfit: 520_000d));
        var check   = Check(outcome.Report, BalanceIntegrityService.NetProfitReconciliationCheck);

        Assert.False(check.Passed);
        Assert.True(check.SignFlip);
        Assert.True(check.ColumnMix);
        Assert.False(BalanceIntegrityService.FailsOnlyOnPresentation(
            [BalanceIntegrityService.NetProfitReconciliationCheck],
            BalanceIntegrityService.NetProfitSignFlipOf(outcome.Report.Checks),
            BalanceIntegrityService.NetProfitColumnMixOf(outcome.Report.Checks)));
    }

    [Theory]
    [InlineData(800_000d, -280_000d, 520_000d,  true)]
    [InlineData(800_000d,  280_000d, 520_000d,  true)]    // magnitude reading
    [InlineData(800_000d, -280_000d, 520_040d,  true)]    // within 0.01 %
    [InlineData(800_000d, -280_000d, 520_200d,  false)]   // 0.04 % off: the column-swap bar, not the 1 % one
    [InlineData(800_000d, -280_000d, -520_000d, false)]   // the prior year's loss is not a match
    public void NetProfitColumnMix_UsesTheColumnSwapTolerance(double resultBeforeTax, double incomeTax, double prior, bool expected)
        => Assert.Equal(expected, BalanceIntegrityService.NetProfitColumnMix(resultBeforeTax, incomeTax, prior));

    [Fact]
    public void NetProfitColumnMixOf_IsNullWhenNetProfitDidNotFail_AndWhenNothingWasRecorded()
    {
        Assert.Null(BalanceIntegrityService.NetProfitColumnMixOf(
            [new BalanceIntegrityCheck { Name = BalanceIntegrityService.NetProfitReconciliationCheck, Passed = true, ColumnMix = true }]));
        Assert.Null(BalanceIntegrityService.NetProfitColumnMixOf(
            [new BalanceIntegrityCheck { Name = BalanceIntegrityService.NetProfitReconciliationCheck, Passed = false }]));
        Assert.Null(BalanceIntegrityService.NetProfitColumnMixOf(null));
    }

    // ── Signs ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Signs_NegativeAssetsLiabilitiesInventoryAndRevenueFail()
    {
        var balance = new ExtractedBalance
        {
            Current = new BalanceAccounts { TotalAssets = -5d, TotalLiabilities = -2d, Inventory = -1d, NetRevenue = -10d }
        };

        var outcome = _sut.Evaluate(balance);

        Assert.False(Check(outcome.Report, BalanceIntegrityService.SignAssetsCheck).Passed);
        Assert.False(Check(outcome.Report, BalanceIntegrityService.SignLiabilitiesCheck).Passed);
        Assert.False(Check(outcome.Report, BalanceIntegrityService.SignInventoryCheck).Passed);
        Assert.False(Check(outcome.Report, BalanceIntegrityService.SignNetRevenueCheck).Passed);
        Assert.Contains("El activo total informado (-$5) debería ser mayor que cero.", outcome.Anomalies);
        Assert.Contains("El pasivo total informado (-$2) no puede ser negativo.", outcome.Anomalies);
        Assert.Contains("Los bienes de cambio informados (-$1) no pueden ser negativos.", outcome.Anomalies);
        Assert.Contains("Las ventas netas informadas (-$10) no pueden ser negativas.", outcome.Anomalies);
        Assert.Contains("margen_neto", outcome.Report.AffectedRatios);
    }

    /// <summary>
    /// A holding, a pre-operating company or a year without sales reports zero sales. That is not an
    /// extraction problem, so it must not turn the analysis to Warning or cap its confidence.
    /// </summary>
    [Fact]
    public void Signs_ZeroRevenuePasses()
    {
        var outcome = _sut.Evaluate(new ExtractedBalance { Current = new BalanceAccounts { NetRevenue = 0d } });

        Assert.True(Check(outcome.Report, BalanceIntegrityService.SignNetRevenueCheck).Passed);
        Assert.True(outcome.Report.Passed);
        Assert.Empty(outcome.Anomalies);
    }

    [Fact]
    public void Signs_ZeroInventoryPasses()
    {
        var outcome = _sut.Evaluate(new ExtractedBalance { Current = new BalanceAccounts { Inventory = 0d } });

        Assert.True(Check(outcome.Report, BalanceIntegrityService.SignInventoryCheck).Passed);
    }

    /// <summary>
    /// A newly incorporated company in its first fiscal year with no debts prints "–" for the total
    /// pasivo. Read as zero with an equation that closes, it is a correct reading: no check may fail,
    /// so the analysis is not turned to Warning, its confidence is not capped and its score does not
    /// turn provisional.
    /// </summary>
    [Fact]
    public void Signs_ZeroLiabilities_WithAClosingEquation_PassesEveryCheck()
    {
        var balance = new ExtractedBalance
        {
            Current = new BalanceAccounts
            {
                TotalAssets = 1_000d, CurrentAssets = 600d, NonCurrentAssets = 400d,
                TotalLiabilities = 0d, CurrentLiabilities = 0d, NonCurrentLiabilities = 0d,
                Equity = 1_000d, NetRevenue = 2_000d, NetProfit = 100d, Inventory = 50d,
            }
        };

        var outcome = _sut.Evaluate(balance);

        Assert.True(outcome.Report.Passed);
        Assert.All(outcome.Report.Checks, c => Assert.True(c.Passed, c.Name));
        Assert.Empty(outcome.Anomalies);
        Assert.Empty(outcome.Report.AffectedRatios);

        var sign = Check(outcome.Report, BalanceIntegrityService.SignLiabilitiesCheck);
        Assert.Equal(0d, sign.Actual);
        Assert.True(Check(outcome.Report, BalanceIntegrityService.LiabilitiesComponentsCheck).Passed);
        Assert.True(Check(outcome.Report, BalanceIntegrityService.AccountingEquationCheck).Passed);
    }

    [Fact]
    public void Signs_NegativeLiabilities_FailWithTheNewMessage()
    {
        var outcome = _sut.Evaluate(new ExtractedBalance { Current = new BalanceAccounts { TotalLiabilities = -5d } });

        Assert.False(Check(outcome.Report, BalanceIntegrityService.SignLiabilitiesCheck).Passed);
        Assert.False(outcome.Report.Passed);
        Assert.Equal(["El pasivo total informado (-$5) no puede ser negativo."], outcome.Anomalies);
        Assert.Equal(["endeudamiento", "leverage"], outcome.Report.AffectedRatios);
    }

    /// <summary>Only the liabilities sign was relaxed: zero total or current assets still fails.</summary>
    [Theory]
    [InlineData(BalanceIntegrityService.SignAssetsCheck)]
    [InlineData(BalanceIntegrityService.SignCurrentAssetsCheck)]
    public void Signs_ZeroAssetsStillFail(string check)
    {
        var accounts = check == BalanceIntegrityService.SignAssetsCheck
            ? new BalanceAccounts { TotalAssets = 0d }
            : new BalanceAccounts { CurrentAssets = 0d };

        var outcome = _sut.Evaluate(new ExtractedBalance { Current = accounts });

        Assert.False(Check(outcome.Report, check).Passed);
        Assert.False(outcome.Report.Passed);
    }

    /// <summary>A holding whose assets are all non-current (participations) reports zero activo corriente.</summary>
    [Fact]
    public void Signs_ZeroCurrentAssetsWithPositiveTotalAssetsPass()
    {
        var balance = new ExtractedBalance
        {
            Current = new BalanceAccounts
            {
                TotalAssets = 800_000d, CurrentAssets = 0d, NonCurrentAssets = 800_000d,
                TotalLiabilities = 100_000d, Equity = 700_000d,
            }
        };

        var outcome = _sut.Evaluate(balance);

        Assert.True(Check(outcome.Report, BalanceIntegrityService.SignCurrentAssetsCheck).Passed);
        Assert.True(outcome.Report.Passed);
        Assert.Empty(outcome.Anomalies);
    }

    [Fact]
    public void Signs_NegativeCurrentAssetsFail()
    {
        var balance = new ExtractedBalance
        {
            Current = new BalanceAccounts { TotalAssets = 800_000d, CurrentAssets = -7d }
        };

        var outcome = _sut.Evaluate(balance);

        Assert.False(Check(outcome.Report, BalanceIntegrityService.SignCurrentAssetsCheck).Passed);
        Assert.Equal(["El activo corriente informado (-$7) no puede ser negativo."], outcome.Anomalies);
        Assert.Equal(["liquidez_corriente", "prueba_acida"], outcome.Report.AffectedRatios);
    }

    /// <summary>Zero activo corriente is legitimate only beside a positive activo total.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    public void Signs_ZeroCurrentAssetsWithoutPositiveTotalAssetsStillFail(double? totalAssets)
    {
        var balance = new ExtractedBalance
        {
            Current = new BalanceAccounts { TotalAssets = totalAssets, CurrentAssets = 0d }
        };

        var outcome = _sut.Evaluate(balance);

        Assert.False(Check(outcome.Report, BalanceIntegrityService.SignCurrentAssetsCheck).Passed);
        Assert.Contains("El activo corriente informado ($0) debería ser mayor que cero.", outcome.Anomalies);
    }

    // ── Column swap ───────────────────────────────────────────────────────────

    [Fact]
    public void ColumnSwap_EveryTotalEqualToThePriorYear_IsTheSameColumnReadTwice()
    {
        var figures = new BalanceAccounts
        {
            TotalAssets = 1_000_000d, CurrentAssets = 600_000d, NonCurrentAssets = 400_000d,
            TotalLiabilities = 700_000d, Equity = 300_000d,
        };

        var outcome = _sut.Evaluate(new ExtractedBalance { Current = figures, PriorYear = figures });

        var swap = Check(outcome.Report, BalanceIntegrityService.ColumnSwapCheck);
        Assert.False(swap.Passed);
        Assert.Equal("prior_year", swap.ComparedWith);
        Assert.Contains(
            "Los totales informados para el ejercicio coinciden con los del ejercicio anterior; es posible " +
            "que se haya leído la misma columna para ambos ejercicios.",
            outcome.Anomalies);
        Assert.Equal(["liquidez_corriente", "prueba_acida", "endeudamiento", "leverage", "roe", "roa"],
            outcome.Report.AffectedRatios);
    }

    [Fact]
    public void ColumnSwap_AnUnchangedLineIsNotASwap()
    {
        var current = new BalanceAccounts { TotalAssets = 1_000_000d, Equity = 300_000d, TotalLiabilities = 700_000d };
        var prior   = new BalanceAccounts { TotalAssets = 900_000d,   Equity = 300_000d, TotalLiabilities = 600_000d };

        var outcome = _sut.Evaluate(new ExtractedBalance { Current = current, PriorYear = prior });

        Assert.True(Check(outcome.Report, BalanceIntegrityService.ColumnSwapCheck).Passed);
        Assert.True(outcome.Report.Passed);
    }

    [Fact]
    public void ColumnSwap_ZerosAreNeverEvidence()
    {
        var current = new BalanceAccounts { TotalAssets = 1_000d, NonCurrentLiabilities = 0d, CurrentAssets = 1_000d };
        var prior   = new BalanceAccounts { TotalAssets = 800d,   NonCurrentAssets = 0d,      CurrentAssets = 800d };

        var outcome = _sut.Evaluate(new ExtractedBalance { Current = current, PriorYear = prior });

        Assert.True(Check(outcome.Report, BalanceIntegrityService.ColumnSwapCheck).Passed);
    }

    // ── A section the statement does not print (inferred zero) ────────────────
    // Read through the serialized check, the shape persisted in BalanceJson.integrity.checks.

    private static string[]? InferredZero(BalanceIntegrityCheck check) =>
        JsonSerializer.SerializeToNode(check)!["inferredZero"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    public static TheoryData<IReadOnlyList<BalanceLineItem>?> NoRubros => new() { null, new List<BalanceLineItem>() };

    /// <summary>
    /// A first fiscal year with only short-term debt: the statement prints no "Pasivo no corriente"
    /// section, the first pass leaves that subtotal null, and liabilities_components used to be skipped
    /// — so the balance read "Sin verificar".
    /// </summary>
    [Theory]
    [MemberData(nameof(NoRubros))]
    public void InferredZero_UnprintedNonCurrentLiabilities_RunsTheCheckWithZero(IReadOnlyList<BalanceLineItem>? rubros)
    {
        var balance = new ExtractedBalance
        {
            Current = new BalanceAccounts { TotalLiabilities = 400_000d, CurrentLiabilities = 400_000d },
            NonCurrentLiabilitiesItems = rubros,
        };

        var outcome = _sut.Evaluate(balance);

        var check = Check(outcome.Report, BalanceIntegrityService.LiabilitiesComponentsCheck);
        Assert.True(check.Passed);
        Assert.Equal(400_000d, check.Expected);
        Assert.Equal(400_000d, check.Actual);
        Assert.Equal(["non_current_liabilities"], InferredZero(check)!);
        Assert.DoesNotContain(BalanceIntegrityService.LiabilitiesComponentsCheck, outcome.Report.Skipped);
        Assert.Empty(outcome.Anomalies);

        // Inside the check only: the extracted figure itself stays unprinted.
        Assert.Null(balance.Current.NonCurrentLiabilities);
    }

    [Fact]
    public void InferredZero_UnprintedCurrentLiabilities_RunsTheCheckWithZero()
    {
        var balance = new ExtractedBalance
        {
            Current = new BalanceAccounts { TotalLiabilities = 250_000d, NonCurrentLiabilities = 250_000d },
        };

        var check = Check(_sut.Evaluate(balance).Report, BalanceIntegrityService.LiabilitiesComponentsCheck);

        Assert.True(check.Passed);
        Assert.Equal(["current_liabilities"], InferredZero(check)!);
    }

    /// <summary>A holding whose statement prints no "Activo corriente" section.</summary>
    [Theory]
    [MemberData(nameof(NoRubros))]
    public void InferredZero_UnprintedCurrentAssets_RunsTheCheckWithZero(IReadOnlyList<BalanceLineItem>? rubros)
    {
        var balance = new ExtractedBalance
        {
            Current = new BalanceAccounts { TotalAssets = 900_000d, NonCurrentAssets = 900_000d },
            CurrentAssetsItems = rubros,
        };

        var outcome = _sut.Evaluate(balance);

        var check = Check(outcome.Report, BalanceIntegrityService.AssetsComponentsCheck);
        Assert.True(check.Passed);
        Assert.Equal(["current_assets"], InferredZero(check)!);
        Assert.DoesNotContain(BalanceIntegrityService.AssetsComponentsCheck, outcome.Report.Skipped);
    }

    [Fact]
    public void InferredZero_UnprintedNonCurrentAssets_RunsTheCheckWithZero()
    {
        var balance = new ExtractedBalance
        {
            Current = new BalanceAccounts { TotalAssets = 300_000d, CurrentAssets = 300_000d },
        };

        var check = Check(_sut.Evaluate(balance).Report, BalanceIntegrityService.AssetsComponentsCheck);

        Assert.True(check.Passed);
        Assert.Equal(["non_current_assets"], InferredZero(check)!);
    }

    [Fact]
    public void InferredZero_WithinTheExistingTolerance()
    {
        var balance = new ExtractedBalance
        {
            Current = new BalanceAccounts { TotalLiabilities = 100_900d, CurrentLiabilities = 100_000d },
        };

        var check = Check(_sut.Evaluate(balance).Report, BalanceIntegrityService.LiabilitiesComponentsCheck);

        Assert.True(check.Passed);
        Assert.Equal(["non_current_liabilities"], InferredZero(check)!);
    }

    [Theory]
    [InlineData(101_200d)] // 1,19 % off: the present component is not the whole total
    [InlineData(60_000d)]
    public void InferredZero_DoesNotApplyWhenThePresentComponentDiffersFromTheTotal(double currentLiabilities)
    {
        var balance = new ExtractedBalance
        {
            Current = new BalanceAccounts { TotalLiabilities = 100_000d, CurrentLiabilities = currentLiabilities },
        };

        var outcome = _sut.Evaluate(balance);

        Assert.Contains(BalanceIntegrityService.LiabilitiesComponentsCheck, outcome.Report.Skipped);
        Assert.DoesNotContain(outcome.Report.Checks, c => c.Name == BalanceIntegrityService.LiabilitiesComponentsCheck);
    }

    [Fact]
    public void InferredZero_DoesNotApplyWhenTheNullComponentHasRubros()
    {
        var balance = new ExtractedBalance
        {
            Current = new BalanceAccounts { TotalLiabilities = 400_000d, CurrentLiabilities = 400_000d },
            NonCurrentLiabilitiesItems = [new() { Label = "Préstamos", Amount = 0d }],
        };

        var check = Check(_sut.Evaluate(balance).Report, BalanceIntegrityService.LiabilitiesComponentsCheck);

        // Was skipped until the rubros of a null subtotal were evaluated: zero rubros now explain the total,
        // recorded as inferredFromItems (the section IS printed), never as inferredZero.
        Assert.True(check.Passed);
        Assert.Null(InferredZero(check));
        Assert.Equal(["non_current_liabilities"], InferredFromItems(check)!);
    }

    [Fact]
    public void InferredZero_DoesNotApplyToTheCurrentAssetsWhenTheyHaveRubros()
    {
        var balance = new ExtractedBalance
        {
            Current = new BalanceAccounts { TotalAssets = 900_000d, NonCurrentAssets = 900_000d },
            CurrentAssetsItems = [new() { Label = "Caja y bancos", Amount = 0d }],
        };

        var check = Check(_sut.Evaluate(balance).Report, BalanceIntegrityService.AssetsComponentsCheck);

        // Was skipped until the rubros of a null subtotal were evaluated: see the liabilities case above.
        Assert.True(check.Passed);
        Assert.Null(InferredZero(check));
        Assert.Equal(["current_assets"], InferredFromItems(check)!);
    }

    [Fact]
    public void InferredZero_DoesNotApplyWithoutATotalOrWithBothComponentsMissing()
    {
        var noTotal  = new BalanceAccounts { CurrentLiabilities = 400_000d, CurrentAssets = 400_000d };
        var noAnyone = new BalanceAccounts { TotalLiabilities = 400_000d, TotalAssets = 400_000d };

        foreach (var figures in new[] { noTotal, noAnyone })
        {
            var outcome = _sut.Evaluate(new ExtractedBalance { Current = figures });

            Assert.Contains(BalanceIntegrityService.LiabilitiesComponentsCheck, outcome.Report.Skipped);
            Assert.Contains(BalanceIntegrityService.AssetsComponentsCheck, outcome.Report.Skipped);
        }
    }

    [Fact]
    public void InferredZero_IsOmittedWhenBothComponentsWerePrinted()
    {
        var balance = new ExtractedBalance
        {
            Current = new BalanceAccounts
            {
                TotalLiabilities = 400_000d, CurrentLiabilities = 400_000d, NonCurrentLiabilities = 0d,
            },
        };

        var check = Check(_sut.Evaluate(balance).Report, BalanceIntegrityService.LiabilitiesComponentsCheck);

        Assert.True(check.Passed);
        Assert.Null(InferredZero(check));
        Assert.DoesNotContain("inferredZero", JsonSerializer.Serialize(check));
    }

    [Fact]
    public void InferredZero_TheStoredReportReadsAsVerified()
    {
        var balance = new ExtractedBalance
        {
            Current = new BalanceAccounts
            {
                TotalAssets = 1_000_000d, CurrentAssets = 1_000_000d,
                TotalLiabilities = 400_000d, CurrentLiabilities = 400_000d,
                Equity = 600_000d,
            },
        };

        var report = _sut.Evaluate(balance).Report;
        var blob   = new JsonObject { ["integrity"] = JsonSerializer.SerializeToNode(report) }.ToJsonString();

        Assert.Contains("\"inferredZero\":[\"non_current_liabilities\"]", blob);
        Assert.Contains("\"inferredZero\":[\"non_current_assets\"]", blob);

        var read = ExtractionIntegrityReader.Read(blob)!;
        Assert.True(read.Verified);
        Assert.Empty(read.NotVerifiedReasons);
        Assert.True(read.Passed);
    }

    // ── A null subtotal evaluated with its rubros (inferred from items) ────────
    // The synthetic shape of a landscape statement read across a portrait page: assets on the left half,
    // liabilities on the right, on the same rows. The pasivo corriente equals the total and the pasivo no
    // corriente is printed as zero, but the first pass left that subtotal null and put the fixed-assets figure
    // of the same row under its only rubro.

    private static string[]? InferredFromItems(BalanceIntegrityCheck check) =>
        JsonSerializer.SerializeToNode(check)!["inferredFromItems"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    private static BalanceLineItem Rubro(string label, double? amount) => new() { Label = label, Amount = amount };

    private static ExtractedBalance LandscapeMisread(params BalanceLineItem[] nonCurrentRubros) => new()
    {
        Current = new BalanceAccounts
        {
            TotalAssets = 2_000_000d, CurrentAssets = 1_300_000d, NonCurrentAssets = 700_000d,
            TotalLiabilities = 800_000d, CurrentLiabilities = 800_000d,
            Equity = 1_200_000d,
        },
        NonCurrentLiabilitiesItems = nonCurrentRubros,
    };

    [Fact]
    public void InferredFromItems_ANonZeroRubroUnderANullSubtotal_ContradictsTheTotalAndFails()
    {
        var balance = LandscapeMisread(Rubro("Otros pasivos", 700_000d));

        var outcome = _sut.Evaluate(balance);

        var check = Check(outcome.Report, BalanceIntegrityService.LiabilitiesComponentsCheck);
        Assert.False(check.Passed);
        Assert.Equal(1_500_000d, check.Expected);
        Assert.Equal(800_000d, check.Actual);
        Assert.Equal(["non_current_liabilities"], InferredFromItems(check)!);
        Assert.Null(InferredZero(check));
        Assert.False(outcome.Report.Passed);
        Assert.DoesNotContain(BalanceIntegrityService.LiabilitiesComponentsCheck, outcome.Report.Skipped);

        // A structure check: it triggers the second pass, the one that turns and splits the pages.
        Assert.Contains(BalanceIntegrityService.LiabilitiesComponentsCheck, ClaudeService.FailedStructureChecks(outcome.Report));

        var anomaly = Assert.Single(outcome.Anomalies);
        Assert.Equal(
            "El pasivo total informado ($800.000) no coincide con la suma del pasivo corriente y los rubros del " +
            "pasivo no corriente ($1.500.000).",
            anomaly);
        Assert.DoesNotContain("%", anomaly);

        // Inside the check only: the extracted figures stay as read.
        Assert.Null(balance.Current.NonCurrentLiabilities);
        Assert.Equal(700_000d, balance.NonCurrentLiabilitiesItems![0].Amount);
    }

    [Fact]
    public void InferredFromItems_RubrosThatExplainTheDifference_Pass()
    {
        var balance = LandscapeMisread(Rubro("Préstamos", 120_000d), Rubro("Otros pasivos", 80_000d))
            with { Current = LandscapeMisread().Current with { CurrentLiabilities = 600_000d } };

        var outcome = _sut.Evaluate(balance);

        var check = Check(outcome.Report, BalanceIntegrityService.LiabilitiesComponentsCheck);
        Assert.True(check.Passed);
        Assert.Equal(800_000d, check.Expected);
        Assert.Equal(["non_current_liabilities"], InferredFromItems(check)!);
        Assert.Empty(outcome.Anomalies);
    }

    [Fact]
    public void InferredFromItems_RubrosAllZero_PassAsAZeroSection()
    {
        var check = Check(_sut.Evaluate(LandscapeMisread(Rubro("Otros pasivos", 0d))).Report,
            BalanceIntegrityService.LiabilitiesComponentsCheck);

        Assert.True(check.Passed);
        Assert.Equal(800_000d, check.Expected);
        Assert.Equal(["non_current_liabilities"], InferredFromItems(check)!);
        Assert.Null(InferredZero(check));
    }

    [Fact]
    public void InferredFromItems_ANullCurrentSubtotalWithRubros_IsEvaluatedWithThem()
    {
        var balance = new ExtractedBalance
        {
            Current = new BalanceAccounts { TotalLiabilities = 500_000d, NonCurrentLiabilities = 200_000d },
            CurrentLiabilitiesItems = [Rubro("Deudas comerciales", 250_000d), Rubro("Cargas fiscales", 100_000d)],
        };

        var outcome = _sut.Evaluate(balance);

        var check = Check(outcome.Report, BalanceIntegrityService.LiabilitiesComponentsCheck);
        Assert.False(check.Passed);
        Assert.Equal(550_000d, check.Expected);
        Assert.Equal(["current_liabilities"], InferredFromItems(check)!);
        Assert.Equal(
            "El pasivo total informado ($500.000) no coincide con la suma de los rubros del pasivo corriente y el " +
            "pasivo no corriente ($550.000).",
            Assert.Single(outcome.Anomalies));
    }

    [Theory]
    [InlineData(400_000d, true)]  // the rubros explain the total
    [InlineData(650_000d, false)] // they contradict it
    public void InferredFromItems_ANullCurrentAssetsSubtotal_IsEvaluatedWithItsRubros(double secondRubro, bool passes)
    {
        var balance = new ExtractedBalance
        {
            Current = new BalanceAccounts { TotalAssets = 1_000_000d, NonCurrentAssets = 300_000d },
            CurrentAssetsItems = [Rubro("Caja y bancos", 300_000d), Rubro("Créditos por ventas", secondRubro)],
        };

        var outcome = _sut.Evaluate(balance);

        var check = Check(outcome.Report, BalanceIntegrityService.AssetsComponentsCheck);
        Assert.Equal(passes, check.Passed);
        Assert.Equal(300_000d + 300_000d + secondRubro, check.Expected);
        Assert.Equal(["current_assets"], InferredFromItems(check)!);
        if (!passes)
        {
            Assert.Equal(
                "El activo total informado ($1.000.000) no coincide con la suma de los rubros del activo corriente y " +
                "el activo no corriente ($1.250.000).",
                Assert.Single(outcome.Anomalies));
        }
    }

    [Fact]
    public void InferredFromItems_ARubroWithoutAnAmount_KeepsTheCheckSkipped()
    {
        var outcome = _sut.Evaluate(LandscapeMisread(Rubro("Préstamos", 0d), Rubro("Otros pasivos", null)));

        Assert.Contains(BalanceIntegrityService.LiabilitiesComponentsCheck, outcome.Report.Skipped);
        Assert.DoesNotContain(outcome.Report.Checks, c => c.Name == BalanceIntegrityService.LiabilitiesComponentsCheck);
    }

    /// <summary>
    /// A trial balance's credit-signed rubros under a null subtotal are never sign-normalized (the normalizer
    /// flips rubros against a printed positive subtotal only): a negative sum proves nothing, so the check keeps
    /// the skip instead of failing a correct reading.
    /// </summary>
    [Fact]
    public void InferredFromItems_ANegativeRubroSum_KeepsTheCheckSkipped()
    {
        var outcome = _sut.Evaluate(LandscapeMisread(Rubro("Préstamos", -150_000d), Rubro("Otros pasivos", -50_000d))
            with { Current = LandscapeMisread().Current with { CurrentLiabilities = 600_000d } });

        Assert.Contains(BalanceIntegrityService.LiabilitiesComponentsCheck, outcome.Report.Skipped);
        Assert.Empty(outcome.Anomalies);
    }

    [Fact]
    public void InferredFromItems_BothSubtotalsNull_StaysSkippedEvenWithRubros()
    {
        var balance = new ExtractedBalance
        {
            Current = new BalanceAccounts { TotalLiabilities = 800_000d },
            CurrentLiabilitiesItems = [Rubro("Deudas comerciales", 800_000d)],
            NonCurrentLiabilitiesItems = [Rubro("Otros pasivos", 0d)],
        };

        Assert.Contains(BalanceIntegrityService.LiabilitiesComponentsCheck, _sut.Evaluate(balance).Report.Skipped);
    }

    [Fact]
    public void InferredFromItems_IsOmittedWhenNothingWasInferredFromRubros()
    {
        var balance = new ExtractedBalance
        {
            Current = new BalanceAccounts
            {
                TotalLiabilities = 400_000d, CurrentLiabilities = 300_000d, NonCurrentLiabilities = 100_000d,
            },
            NonCurrentLiabilitiesItems = [Rubro("Préstamos", 100_000d)],
        };

        var check = Check(_sut.Evaluate(balance).Report, BalanceIntegrityService.LiabilitiesComponentsCheck);

        Assert.Null(check.InferredFromItems);
        Assert.DoesNotContain("inferredFromItems", JsonSerializer.Serialize(check));
    }

    [Fact]
    public void InferredFromItems_AContradiction_ReadsAsVerifiedAndFailed()
    {
        var report = _sut.Evaluate(LandscapeMisread(Rubro("Otros pasivos", 700_000d))).Report;
        var blob   = new JsonObject { ["integrity"] = JsonSerializer.SerializeToNode(report) }.ToJsonString();

        Assert.Contains("\"inferredFromItems\":[\"non_current_liabilities\"]", blob);

        var read = ExtractionIntegrityReader.Read(blob)!;
        Assert.True(read.Verified);
        Assert.Empty(read.NotVerifiedReasons);
        Assert.False(read.Passed);
        Assert.Contains(BalanceIntegrityService.LiabilitiesComponentsCheck, read.FailedChecks);
        Assert.False(read.PresentationOnly);
    }

    /// <summary>A blob stored before the rule existed: the check was skipped then, and reads exactly as before.</summary>
    [Fact]
    public void InferredFromItems_AStoredBlobWithoutTheField_ReadsAsBefore()
    {
        const string blob =
            "{\"integrity\":{\"passed\":true,\"checks\":[" +
            "{\"name\":\"assets_components\",\"passed\":true,\"expected\":2000000,\"actual\":2000000,\"diffPct\":0,\"lines\":[\"assets\"]}," +
            "{\"name\":\"accounting_equation\",\"passed\":true,\"expected\":2000000,\"actual\":2000000,\"diffPct\":0,\"lines\":[\"assets\"]}]," +
            "\"skipped\":[\"liabilities_components\",\"non_current_liabilities_items\"],\"affectedRatios\":[]}}";

        var read = ExtractionIntegrityReader.Read(blob)!;

        Assert.True(read.Passed);
        Assert.False(read.Verified);
        Assert.Equal(["liabilities_components_skipped"], read.NotVerifiedReasons);
    }

    // ── Older model output ────────────────────────────────────────────────────

    [Fact]
    public void OlderOutput_WithoutRubrosOrPriorYear_RunsOnlyTheChecksThatApply()
    {
        var balance = new ExtractedBalance
        {
            Current = new BalanceAccounts
            {
                TotalAssets = 1_000d, CurrentAssets = 600d, NonCurrentAssets = 400d,
                TotalLiabilities = 700d, CurrentLiabilities = 500d, NonCurrentLiabilities = 200d,
                Equity = 300d, NetRevenue = 2_000d, NetProfit = 100d, Inventory = 50d,
            }
        };

        var outcome = _sut.Evaluate(balance);

        Assert.True(outcome.Report.Passed);
        Assert.Empty(outcome.Anomalies);
        Assert.Equal(
            [
                BalanceIntegrityService.CurrentAssetsItemsCheck,
                BalanceIntegrityService.CurrentLiabilitiesItemsCheck,
                BalanceIntegrityService.NonCurrentLiabilitiesItemsCheck,
                BalanceIntegrityService.NetProfitReconciliationCheck,
                BalanceIntegrityService.ColumnSwapCheck,
            ],
            outcome.Report.Skipped);
        Assert.Contains(outcome.Report.Checks, c => c.Name == BalanceIntegrityService.AccountingEquationCheck);
    }

    [Fact]
    public void NothingExtracted_SkipsEverythingAndPasses()
    {
        var outcome = _sut.Evaluate(new ExtractedBalance { Current = new BalanceAccounts() });

        Assert.True(outcome.Report.Passed);
        Assert.Empty(outcome.Report.Checks);
        Assert.Empty(outcome.Anomalies);
        Assert.Equal(13, outcome.Report.Skipped.Count);
    }

    // ── Anomaly hygiene ───────────────────────────────────────────────────────

    [Fact]
    public void Anomalies_NeverCarryAPercentSign_SoTheNarrativeReconcilerLeavesThemAlone()
    {
        var balance = ColumnSwapCase() with
        {
            Current = ColumnSwapCurrentYear() with { TotalAssets = -1d, NetRevenue = -1d, NetProfit = 5d },
            ResultBeforeTax = 100d,
            IncomeTax = -10d,
        };

        var outcome = _sut.Evaluate(balance);

        Assert.NotEmpty(outcome.Anomalies);
        Assert.All(outcome.Anomalies, a => Assert.DoesNotContain("%", a));
    }
}
