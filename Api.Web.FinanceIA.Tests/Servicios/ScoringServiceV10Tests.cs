// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using System.Text.Json;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.ScoringTestHarness;
using Codes = Api.Web.FinanceIA.Servicios.ScoreReliability.ScoreReliabilityCodes;
using PigDocument = UglyToad.PdfPig.PdfDocument;
using ReliabilityFixture = Api.Web.FinanceIA.Tests.Servicios.ScoreReliability.ScoreReliabilityFixture;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Pins scoring algorithm version 10, both halves validated with the SGR (03/11):
/// <list type="bullet">
/// <item><b>A</b> — current liabilities read as zero against a positive numerator is the BEST liquidity, not
/// a division by zero: liquidez corriente / prueba ácida stay null in value, carry the
/// <c>no_current_liabilities</c> marker, and score exactly what a ratio sitting on its OK threshold scores —
/// measured, weighted, never critical, never a reliability caveat.</item>
/// <item><b>B</b> — ROE, ROA and margen neto keep scoring a net loss in the composite, but their critical
/// floor applies only when the operating result is not positive (or unread), or when interest printed on
/// its own line consumes it. <c>critical_floor_profitability_requires_operating_loss = 0</c> is version 9.</item>
/// </list>
/// Every balance goes through the real <see cref="RatioCalculationService"/>, because the marker and the
/// operating result reach the engine only that way.
/// </summary>
public sealed class ScoringServiceV10Tests
{
    private const string FlagKey = "critical_floor_profitability_requires_operating_loss";

    private static readonly RatioCalculationService Ratios = new();

    // ── Balances ──────────────────────────────────────────────────────────────────────────────

    // A first fiscal year with no debts: cash and stock, current liabilities 0 (read, not missing).
    // Interest coverage is unread (no financial expenses line), so the renormalise path is the one taken.
    private static BalanceAccounts FirstYearNoDebts() => new()
    {
        CurrentAssets         = 500,
        NonCurrentAssets      = 500,
        TotalAssets           = 1_000,
        CurrentLiabilities    = 0,
        NonCurrentLiabilities = 0,
        TotalLiabilities      = 0,
        Equity                = 1_000,
        NetRevenue            = 2_000,
        NetProfit             = 100,
        OperatingResult       = 150,
        FinancialExpenses     = null,
        Inventory             = 100,
        PrepaidExpenses       = 0,
    };

    // TECNOPESCA-like, anonymised and scaled: operating +478, net −94 — the loss is the financial and
    // holding results (RECPAM included). ROE −9.4 %, ROA −4.7 %, margen −3.13 %: all three beyond danger.
    private static BalanceAccounts RecpamLossIndustrial() => new()
    {
        CurrentAssets      = 900,
        TotalAssets        = 2_000,
        CurrentLiabilities = 600,
        TotalLiabilities   = 1_000,
        Equity             = 1_000,
        NetRevenue         = 3_000,
        NetProfit          = -94,
        OperatingResult    = 478,
        FinancialExpenses  = null,
        Inventory          = 300,
        PrepaidExpenses    = 0,
    };

    private static Task<ScoringResult> ScoreBalance(ScoringService sut, BalanceAccounts balance)
        => Score(sut, Ratios.Calculate(balance), BcraNoDebt(), MacroBenign());

    // ── A. Zero current liabilities ───────────────────────────────────────────────────────────

    [Fact]
    public void RatioCalculation_ZeroCurrentLiabilities_WithAPositiveNumerator_MarksBothLiquidityRatios()
    {
        var r = Ratios.Calculate(FirstYearNoDebts());

        Assert.Null(r.CurrentRatio);   // never an infinite number
        Assert.Null(r.AcidTest);
        Assert.Equal(RatioExclusionReasons.NoCurrentLiabilities, r.ExclusionReasons!["liquidez_corriente"]);
        Assert.Equal(RatioExclusionReasons.NoCurrentLiabilities, r.ExclusionReasons["prueba_acida"]);
        Assert.Equal(150d, r.OperatingResult);
        Assert.Null(r.FinancialExpenses);
    }

    [Fact]
    public async Task ZeroCurrentLiabilities_ScoresBothRatiosExactlyLikeARatioOnItsOkThreshold()
    {
        var sut     = await BuildAsync();
        var marked  = Ratios.Calculate(FirstYearNoDebts());

        // The same balance with the two liquidity ratios set to their OK thresholds and no marker: the
        // engine's own "at or beyond OK" band, which v10 must reproduce without hardcoding a number.
        var atOk = Ratios.Calculate(FirstYearNoDebts());
        atOk.CurrentRatio     = (double)ScoringDefaults.Get("threshold_liquidez_corriente_ok");
        atOk.AcidTest         = (double)ScoringDefaults.Get("threshold_prueba_acida_ok");
        atOk.ExclusionReasons = atOk.ExclusionReasons!
            .Where(kv => kv.Key is not ("liquidez_corriente" or "prueba_acida"))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var r        = await Score(sut, marked, BcraNoDebt(), MacroBenign());
        var expected = await Score(sut, atOk,   BcraNoDebt(), MacroBenign());

        Assert.Equal(expected.Score,                 r.Score);
        Assert.Equal(expected.ScoreRatios,           r.ScoreRatios);
        Assert.Equal(expected.RatiosSurvivingWeight, r.RatiosSurvivingWeight);
        foreach (var key in ScoringDefaults.ThresholdRatios)
        {
            Assert.Equal(Entry(expected, key).Score,           Entry(r, key).Score);
            Assert.Equal(Entry(expected, key).EffectiveWeight, Entry(r, key).EffectiveWeight);
            Assert.Equal(Entry(expected, key).Status,          Entry(r, key).Status);
        }

        foreach (var key in new[] { "liquidez_corriente", "prueba_acida" })
        {
            var entry = Entry(r, key);
            Assert.False(entry.Excluded);
            Assert.Equal(RatioExclusionReasons.NoCurrentLiabilities, entry.ExclusionReason);
            Assert.Null(entry.Measured);
            Assert.Equal("Saludable", entry.Status);
            Assert.DoesNotContain(key, r.NonComputableRatios);
        }

        // Only the unread interest coverage is renormalised away: 1 − 0.15.
        Assert.Equal(["cobertura_intereses"], r.NonComputableRatios);
        Assert.Equal(0.85m, r.RatiosSurvivingWeight);
        Assert.Equal(ScoringService.ScoringAlgoVersion, r.ScoringAlgoVersion);   // v10 behaviour, still true under v11
    }

    [Fact]
    public async Task ZeroCurrentLiabilities_UnderATenantsOwnThresholds_StillScoresTheBestBand()
    {
        var sut = await BuildAsync(
            ("threshold_liquidez_corriente_ok", 2.50m), ("threshold_liquidez_corriente_warn", 1.80m),
            ("threshold_liquidez_corriente_danger", 1.20m));

        var r = await ScoreBalance(sut, FirstYearNoDebts());

        // The best band of THIS triplet — what a current ratio of exactly 2.50 scores.
        var atOk = Ratios.Calculate(FirstYearNoDebts());
        atOk.CurrentRatio     = 2.50;
        atOk.ExclusionReasons = atOk.ExclusionReasons!
            .Where(kv => kv.Key != "liquidez_corriente").ToDictionary(kv => kv.Key, kv => kv.Value);
        var expected = await Score(sut, atOk, BcraNoDebt(), MacroBenign());

        Assert.Equal(Entry(expected, "liquidez_corriente").Score, Entry(r, "liquidez_corriente").Score);
        Assert.Equal(expected.Score, r.Score);
    }

    [Fact]
    public async Task ZeroCurrentLiabilities_IsNeverCritical_EvenWithATriggerOfZero()
    {
        // A trigger of 0 makes every MEASURED ratio critical; the best-band liquidity must stay out.
        var sut = await BuildAsync(("critical_floor_trigger_threshold", 0m));

        var r = await ScoreBalance(sut, FirstYearNoDebts());

        Assert.DoesNotContain("liquidez_corriente", r.CriticalRatios);
        Assert.DoesNotContain("prueba_acida",       r.CriticalRatios);
        Assert.Contains("endeudamiento", r.CriticalRatios);   // the trigger really is 0
    }

    [Fact]
    public async Task ZeroCurrentLiabilities_RaisesNeitherRatiosNotMeaningfulNorRatiosNotMeasured()
    {
        var r = await ScoreBalance(await BuildAsync(), FirstYearNoDebts());

        var verdict = ReliabilityFixture.Evaluate(
            ReliabilityFixture.Row(score: r.Score),
            ReliabilityFixture.Json(r, ReliabilityFixture.CleanFacts()));

        Assert.NotNull(verdict);
        var codes = verdict!.Reasons.Select(reason => reason.Code).ToList();
        Assert.DoesNotContain(Codes.RatiosNotMeaningful, codes);
        Assert.DoesNotContain(Codes.RatiosNotMeasured,   codes);
        Assert.DoesNotContain(Codes.RatiosLowCoverage,   codes);
    }

    [Fact]
    public async Task ZeroCurrentLiabilities_WithZeroCurrentAssets_IsNotMeaningful_AsBefore()
    {
        var balance = FirstYearNoDebts() with { CurrentAssets = 0, Inventory = 0, NonCurrentAssets = 1_000 };

        var ratios = Ratios.Calculate(balance);
        Assert.Equal(RatioExclusionReasons.NotMeaningful, ratios.ExclusionReasons!["liquidez_corriente"]);
        Assert.Equal(RatioExclusionReasons.NotMeaningful, ratios.ExclusionReasons["prueba_acida"]);

        var r = await Score(await BuildAsync(), ratios, BcraNoDebt(), MacroBenign());
        Assert.True(Entry(r, "liquidez_corriente").Excluded);
        Assert.Equal(RatioExclusionReasons.NotMeaningful, Entry(r, "liquidez_corriente").ExclusionReason);
        Assert.True(Entry(r, "prueba_acida").Excluded);
        Assert.Contains("liquidez_corriente", r.NonComputableRatios);
    }

    [Fact]
    public async Task UnreadCurrentLiabilities_IsUnread_AsBefore()
    {
        var ratios = Ratios.Calculate(FirstYearNoDebts() with { CurrentLiabilities = null });

        Assert.Equal(RatioExclusionReasons.Unread, ratios.ExclusionReasons!["liquidez_corriente"]);
        Assert.Equal(RatioExclusionReasons.Unread, ratios.ExclusionReasons["prueba_acida"]);

        var r = await Score(await BuildAsync(), ratios, BcraNoDebt(), MacroBenign());
        Assert.True(Entry(r, "liquidez_corriente").Excluded);
        Assert.Equal(RatioExclusionReasons.Unread, Entry(r, "liquidez_corriente").ExclusionReason);
    }

    [Fact]
    public async Task ZeroCurrentLiabilities_WithAnAcidNumeratorAtOrBelowZero_KeepsAcidTestNotMeaningful()
    {
        // Every current asset is stock: acid numerator 500 − 500 = 0. The current ratio still has a
        // positive numerator, so it alone takes the best band.
        var ratios = Ratios.Calculate(FirstYearNoDebts() with { Inventory = 500 });

        Assert.Equal(RatioExclusionReasons.NoCurrentLiabilities, ratios.ExclusionReasons!["liquidez_corriente"]);
        Assert.Equal(RatioExclusionReasons.NotMeaningful,        ratios.ExclusionReasons["prueba_acida"]);

        var r = await Score(await BuildAsync(), ratios, BcraNoDebt(), MacroBenign());
        Assert.False(Entry(r, "liquidez_corriente").Excluded);
        Assert.True(Entry(r, "prueba_acida").Excluded);
        Assert.Equal(RatioExclusionReasons.NotMeaningful, Entry(r, "prueba_acida").ExclusionReason);
    }

    [Fact]
    public void ZeroCurrentLiabilities_WithNegativeInventory_KeepsAcidTestNotMeaningful()
    {
        var ratios = Ratios.Calculate(FirstYearNoDebts() with { Inventory = -5 });

        Assert.Equal(RatioExclusionReasons.NoCurrentLiabilities, ratios.ExclusionReasons!["liquidez_corriente"]);
        Assert.Equal(RatioExclusionReasons.NotMeaningful,        ratios.ExclusionReasons["prueba_acida"]);
    }

    [Fact]
    public async Task ModelSuppliedRatios_CarryNoMarker_AndANullCurrentRatioIsExcludedAsBefore()
    {
        var ratios = HealthyRatios();
        ratios.CurrentRatio = null;   // no ExclusionReasons at all: the model's own output

        var r = await Score(await BuildAsync(), ratios, BcraNoDebt(), MacroBenign());

        Assert.True(Entry(r, "liquidez_corriente").Excluded);
        Assert.Null(Entry(r, "liquidez_corriente").ExclusionReason);
    }

    // ── B. Profitability floor ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecpamLoss_WithAPositiveOperatingResult_TakesNoProfitabilityFloor_ButStillScoresTheLoss()
    {
        var v10 = await ScoreBalance(await BuildAsync(),                RecpamLossIndustrial());
        var v9  = await ScoreBalance(await BuildAsync((FlagKey, 0m)),   RecpamLossIndustrial());

        // Version 9 (flag off): the three loss ratios are critical and floor the score to 55.
        Assert.Equal(["roe", "roa", "margen_neto"], v9.CriticalRatios);
        Assert.Equal(55m, v9.AppliedFloor);
        Assert.Equal(55m, v9.Score);
        Assert.False(v9.ProfitabilityFloorWaived);
        Assert.Null(v9.OperatingResultSign);

        // Version 10: same composite — the loss is still scored — but no floor.
        Assert.Equal(v9.ScoreRatios,      v10.ScoreRatios);
        Assert.Equal(v9.ScoreBeforeFloor, v10.ScoreBeforeFloor);
        Assert.Empty(v10.CriticalRatios);
        Assert.Equal(0m, v10.AppliedFloor);
        Assert.Equal(v10.ScoreBeforeFloor, v10.Score);
        Assert.True(v10.Score < 55m);

        foreach (var key in new[] { "roe", "roa", "margen_neto" })
        {
            Assert.Equal(100m,      Entry(v10, key).Score);
            Assert.Equal("Critico", Entry(v10, key).Status);   // at danger; it just does not floor
            Assert.False(Entry(v10, key).Excluded);
        }

        Assert.True(v10.ProfitabilityFloorWaived);
        Assert.Equal(["roe", "roa", "margen_neto"], v10.ProfitabilityFloorWaivedRatios);
        Assert.Equal("positive", v10.OperatingResultSign);
    }

    [Theory]
    [InlineData(-120d, "negative")]
    [InlineData(0d,    "zero")]
    public async Task AnOperatingResultThatIsNotPositive_KeepsTheFloor(double operating, string sign)
    {
        var r = await ScoreBalance(await BuildAsync(), RecpamLossIndustrial() with { OperatingResult = operating });

        Assert.Equal(["roe", "roa", "margen_neto"], r.CriticalRatios);
        Assert.Equal(55m, r.Score);
        Assert.False(r.ProfitabilityFloorWaived);
        Assert.Empty(r.ProfitabilityFloorWaivedRatios);
        Assert.Equal(sign, r.OperatingResultSign);
    }

    [Fact]
    public async Task AnUnreadOperatingResult_KeepsTheFloor()
    {
        var r = await ScoreBalance(await BuildAsync(), RecpamLossIndustrial() with { OperatingResult = null });

        Assert.Equal(55m, r.Score);
        Assert.Equal(["roe", "roa", "margen_neto"], r.CriticalRatios);
        Assert.False(r.ProfitabilityFloorWaived);
        Assert.Null(r.OperatingResultSign);   // consulted, but nothing was read
    }

    [Fact]
    public async Task InterestThatConsumesTheOperatingProfit_KeepsTheFloor()
    {
        // Pure interest printed on its own line, larger than the operating profit: financing ate it.
        // Reading the interest also makes interest coverage computable (478 / 500 < 1, beyond danger), so
        // its own 70 floor is the binding one; what matters here is that the profitability ratios floor too.
        var r = await ScoreBalance(await BuildAsync(), RecpamLossIndustrial() with { FinancialExpenses = -500 });

        Assert.Equal(["cobertura_intereses", "roe", "roa", "margen_neto"], r.CriticalRatios);
        Assert.Equal(70m, r.AppliedFloor);
        Assert.False(r.ProfitabilityFloorWaived);
        Assert.Empty(r.ProfitabilityFloorWaivedRatios);
        Assert.Equal("positive", r.OperatingResultSign);
    }

    [Fact]
    public async Task InterestExactlyEqualToTheOperatingProfit_KeepsTheFloor()
    {
        // Positive sign as extracted: the engine takes |financial_expenses|. 478 − 478 = 0 → floor.
        var r = await ScoreBalance(await BuildAsync(), RecpamLossIndustrial() with { FinancialExpenses = 478 });

        Assert.Contains("roe", r.CriticalRatios);
        Assert.False(r.ProfitabilityFloorWaived);
    }

    [Fact]
    public async Task InterestSmallerThanTheOperatingProfit_StillWaivesTheFloor()
    {
        var r = await ScoreBalance(await BuildAsync(), RecpamLossIndustrial() with { FinancialExpenses = -100 });

        Assert.True(r.ProfitabilityFloorWaived);
        Assert.Empty(r.CriticalRatios);
        Assert.Equal(0m, r.AppliedFloor);
    }

    [Fact]
    public async Task AnotherCriticalRatio_StillFloorsNormally_BesideAWaivedProfitabilityFloor()
    {
        // Current ratio 300 / 600 = 0.50, beyond its danger 0.70 → liquidez floors at 60.
        var r = await ScoreBalance(await BuildAsync(), RecpamLossIndustrial() with { CurrentAssets = 300, Inventory = 50 });

        Assert.Contains("liquidez_corriente", r.CriticalRatios);
        Assert.DoesNotContain("roe", r.CriticalRatios);
        Assert.Equal(60m, r.AppliedFloor);
        Assert.Equal(60m, r.Score);
        Assert.True(r.ProfitabilityFloorWaived);
        Assert.Equal(["roe", "roa", "margen_neto"], r.ProfitabilityFloorWaivedRatios);
    }

    [Fact]
    public async Task ModelSuppliedRatios_CarryNoOperatingResult_AndKeepTheVersion9Floor()
    {
        var r = await Score(await BuildAsync(), BeyondDangerRatios(), BcraNoDebt(), MacroBenign());

        Assert.Contains("roe", r.CriticalRatios);
        Assert.False(r.ProfitabilityFloorWaived);
    }

    [Fact]
    public async Task NoProfitabilityRatioAtDanger_LeavesTheRuleUnconsulted()
    {
        var r = await Score(await BuildAsync(), HealthyRatios(), BcraNoDebt(), MacroBenign());

        Assert.False(r.ProfitabilityFloorWaived);
        Assert.Empty(r.ProfitabilityFloorWaivedRatios);
        Assert.Null(r.OperatingResultSign);
    }

    // ── The persisted breakdown ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Breakdown_RecordsTheWaiverAndTheMarker()
    {
        var waived = await ScoreBalance(await BuildAsync(), RecpamLossIndustrial());
        using var waivedJson = JsonDocument.Parse(AnalysisScoringStage.BuildScoreBreakdownJson(waived, null));

        Assert.True(waivedJson.RootElement.GetProperty("profitabilityFloorWaived").GetBoolean());
        Assert.Equal(["roe", "roa", "margen_neto"],
            waivedJson.RootElement.GetProperty("profitabilityFloorWaivedRatios").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("positive", waivedJson.RootElement.GetProperty("operatingResultSign").GetString());
        Assert.Equal(ScoringService.ScoringAlgoVersion, waivedJson.RootElement.GetProperty("scoringAlgoVersion").GetString());

        var marked = await ScoreBalance(await BuildAsync(), FirstYearNoDebts());
        using var markedJson = JsonDocument.Parse(AnalysisScoringStage.BuildScoreBreakdownJson(marked, null));
        var liquidez = markedJson.RootElement.GetProperty("breakdown").GetProperty("liquidez_corriente");

        Assert.False(liquidez.GetProperty("excluded").GetBoolean());
        Assert.Equal("no_current_liabilities", liquidez.GetProperty("exclusionReason").GetString());
        Assert.False(markedJson.RootElement.GetProperty("profitabilityFloorWaived").GetBoolean());
        Assert.Equal(0, markedJson.RootElement.GetProperty("profitabilityFloorWaivedRatios").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, markedJson.RootElement.GetProperty("operatingResultSign").ValueKind);
    }

    // ── Chat, narrative and PDF wording ───────────────────────────────────────────────────────

    [Fact]
    public async Task Chat_ReadsTheMarkerAndTheWaiver()
    {
        var marked = await ScoreBalance(await BuildAsync(), FirstYearNoDebts());
        var markedJson = AnalysisScoringStage.BuildScoreBreakdownJson(marked, null);
        string block = AnalysisController.FormatScoreBreakdownBlock(markedJson, marked.Score)!;

        Assert.Contains($"Liquidez corriente: 1000/1000 — Saludable ({ScoringNoticeTexts.NoCurrentLiabilities})", block);
        Assert.Contains($"Prueba ácida: 1000/1000 — Saludable ({ScoringNoticeTexts.NoCurrentLiabilities})", block);
        Assert.DoesNotContain("no aplica con estas cifras", block);
        Assert.Contains("current_ratio y acid_test en null", AnalysisController.NoCurrentLiabilitiesRatioNote(markedJson));

        var waived = await ScoreBalance(await BuildAsync(), RecpamLossIndustrial());
        var waivedJson = AnalysisScoringStage.BuildScoreBreakdownJson(waived, null);
        string waivedBlock = AnalysisController.FormatScoreBreakdownBlock(waivedJson, waived.Score)!;

        Assert.Contains(ScoringNoticeTexts.ProfitabilityFloorWaived, waivedBlock);
        Assert.Contains("Ratios alcanzados: ROE, ROA, Margen neto", waivedBlock);
        Assert.DoesNotContain("se redujo a", waivedBlock);
        Assert.Null(AnalysisController.NoCurrentLiabilitiesRatioNote(waivedJson));
    }

    [Fact]
    public async Task Narrative_ReadsTheMarkerAndTheWaiver()
    {
        var markedRatios = Ratios.Calculate(FirstYearNoDebts());
        var marked = await Score(await BuildAsync(), markedRatios, BcraNoDebt(), MacroBenign());
        var prompt = ClaudeService.BuildNarrativePrompt(marked, markedRatios, null, null);

        Assert.Contains($"Liquidez corriente (activo corriente / pasivo corriente) : {ScoringNoticeTexts.NoCurrentLiabilities}", prompt);
        Assert.Contains($"Liquidez corriente: Saludable ({ScoringNoticeTexts.NoCurrentLiabilities})", prompt);
        Assert.DoesNotContain("PISO DE RENTABILIDAD NO APLICADO", prompt);

        var waivedRatios = Ratios.Calculate(RecpamLossIndustrial());
        var waived = await Score(await BuildAsync(), waivedRatios, BcraNoDebt(), MacroBenign());
        var waivedPrompt = ClaudeService.BuildNarrativePrompt(waived, waivedRatios, null, null);

        Assert.Contains("PISO DE RENTABILIDAD NO APLICADO", waivedPrompt);
        Assert.Contains(ScoringNoticeTexts.ProfitabilityFloorWaived, waivedPrompt);
        Assert.DoesNotContain("AJUSTE POR RATIO CRÍTICO", waivedPrompt);
        Assert.DoesNotContain(ScoringNoticeTexts.NoCurrentLiabilities, waivedPrompt);
    }

    [Fact]
    public async Task Pdf_ReadsTheMarkerAndTheWaiver()
    {
        var markedRatios = Ratios.Calculate(FirstYearNoDebts());
        var marked = await Score(await BuildAsync(), markedRatios, BcraNoDebt(), MacroBenign());
        string markedText = await RenderPdfText(marked, markedRatios);

        // PdfPig reads a vertically-centred status cell out of row order, so count words rather than
        // matching across cells. "ratios" + "ratio_breakdown" print ONE table: value "Sin pasivo corriente",
        // its score, and the status "Liquidez máxima" for both ratios — the breakdown's own phrase
        // "Sin pasivo corriente: liquidez máxima" would repeat the value cell beside it, so it is not printed.
        Assert.Contains("Liquidez corriente Sin pasivo corriente 1000", markedText);
        Assert.Contains("Prueba ácida Sin pasivo corriente 1000", markedText);
        // Twice in the ratios table, once on the first page's Liquidez corriente tile.
        Assert.Equal(3, Occurrences(markedText, "Liquidez máxima"));
        Assert.Equal(0, Occurrences(markedText, "Sin pasivo corriente:"));
        Assert.Equal(0, Occurrences(markedText, "liquidez máxima"));
        Assert.DoesNotContain("No aplica", markedText);
        Assert.DoesNotContain(ScoringNoticeTexts.ProfitabilityFloorWaived.Split(':')[0], markedText);

        var waivedRatios = Ratios.Calculate(RecpamLossIndustrial());
        var waived = await Score(await BuildAsync(), waivedRatios, BcraNoDebt(), MacroBenign());
        string waivedText = await RenderPdfText(waived, waivedRatios);

        Assert.Contains(ScoringNoticeTexts.ProfitabilityFloorWaived, waivedText);
        Assert.DoesNotContain("Sin pasivo corriente", waivedText);
    }

    private static int Occurrences(string text, string value)
    {
        int count = 0;
        for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static async Task<string> RenderPdfText(ScoringResult scoring, RatiosData ratios)
    {
        var service = new PdfExportService(
            new CashFlowNarrativeService(),
            new CashFlowReconciliationService(),
            new AlertRatioReconciliationService(),
            NullLogger<PdfExportService>.Instance);

        var result = new AnalysisResult
        {
            ResultId           = new Guid("10101010-1010-1010-1010-101010101010"),
            AiSuggestedScore   = scoring.Score,
            AiSuggestedLevel   = scoring.Level,
            RatiosJson         = JsonSerializer.Serialize(ratios),
            ScoreBreakdownJson = AnalysisScoringStage.BuildScoreBreakdownJson(scoring, null),
        };

        byte[] pdf = await service.GenerateAsync(
            result, new Analysis { FiscalYear = 2025 }, "tenant-x", ["risk", "ratios", "ratio_breakdown"],
            CancellationToken.None);

        using PigDocument doc = PigDocument.Open(pdf);
        return string.Join(
            "\n",
            doc.GetPages().Select(page => string.Join(" ", page.GetWords().Select(word => word.Text))));
    }
}
