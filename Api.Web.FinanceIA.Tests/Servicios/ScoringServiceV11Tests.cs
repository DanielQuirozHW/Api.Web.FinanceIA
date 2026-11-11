// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-04

using System.Text.Json;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Lufe;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.ScoringTestHarness;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Pins scoring algorithm version 11: a critical prueba ácida floors the score only while liquidez corriente
/// is measured and strictly below its (sector-merged) warn threshold; otherwise it keeps "Critico" and its
/// weight in the composite but imposes no floor. An unread current ratio keeps the floor. The default
/// <c>critical_floor_prueba_acida</c> is 50 (was 60); <c>critical_floor_acid_test_requires_weak_current_ratio = 0</c>
/// is version 10.
/// <para>
/// Every fixture starts from <see cref="ScoringTestHarness.HealthyRatios"/> (every partial 0) and moves only
/// the two liquidity ratios, so the composite stays well below every floor and a floor that applies is
/// always visible in the score. Synthetic figures only.
/// </para>
/// </summary>
public sealed class ScoringServiceV11Tests
{
    private const string FlagKey  = "critical_floor_acid_test_requires_weak_current_ratio";
    private const string FloorKey = "critical_floor_prueba_acida";

    private static RatiosData Liquidity(double? current, double? acid)
    {
        var ratios = HealthyRatios();
        ratios.CurrentRatio = current;
        ratios.AcidTest     = acid;
        return ratios;
    }

    private static Task<ScoringResult> ScoreLiquidity(
        ScoringService sut, double? current, double? acid, string? sector = null,
        List<LufeVentaCompraMes>? ventas = null)
        => Score(sut, Liquidity(current, acid), BcraNoDebt(), MacroBenign(), sector, ventas);

    // ── Defaults ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Defaults_AcidFloorIs50_LiquidezFloorStays60_FlagIsOn_AndVersionIs11()
    {
        Assert.Equal(50m, ScoringDefaults.Get(FloorKey));
        Assert.Equal(60m, ScoringDefaults.Get("critical_floor_liquidez_corriente"));
        Assert.Equal(1m,  ScoringDefaults.Get(FlagKey));
        Assert.Equal([FlagKey], ScoringDefaults.AcidTestFloorKeys);
        Assert.DoesNotContain(FlagKey, ScoringDefaults.CriticalFloorKeys);
        Assert.Equal("11", ScoringService.ScoringAlgoVersion);
    }

    // ── The rule ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StrongCurrentRatio_CriticalAcidTest_IsWaived_AndTheCompositeStands()
    {
        var r = await ScoreLiquidity(await BuildAsync(), current: 1.94, acid: 0.50);

        Assert.Equal("Critico", Entry(r, "prueba_acida").Status);     // still at danger
        Assert.Equal(100m, Entry(r, "prueba_acida").Score);            // still in the composite
        Assert.Equal(0.10m, Entry(r, "prueba_acida").EffectiveWeight); // at its full weight
        Assert.Empty(r.CriticalRatios);
        Assert.True(r.AcidTestFloorWaived);
        Assert.Equal("at_or_above", r.CurrentRatioVsWarn);
        Assert.Equal(0m, r.AppliedFloor);
        Assert.Equal(r.ScoreBeforeFloor, r.Score);
        Assert.True(r.Score < 50m);                                    // a floor would have shown
        Assert.Equal(50m, r.CriticalFloors["prueba_acida"]);
        Assert.Equal("11", r.ScoringAlgoVersion);
    }

    [Fact]
    public async Task WeakCurrentRatio_CriticalAcidTest_FloorsAt50()
    {
        var r = await ScoreLiquidity(await BuildAsync(), current: 0.84, acid: 0.28);

        Assert.Equal("Atencion", Entry(r, "liquidez_corriente").Status);   // weak, not critical
        Assert.Equal(["prueba_acida"], r.CriticalRatios);
        Assert.False(r.AcidTestFloorWaived);
        Assert.Equal("below", r.CurrentRatioVsWarn);
        Assert.Equal(50m, r.AppliedFloor);
        Assert.True(r.ScoreBeforeFloor < 50m);
        Assert.Equal(50m, r.Score);
    }

    [Fact]
    public async Task CriticalCurrentRatio_AndCriticalAcidTest_FloorAt60FromLiquidez()
    {
        var r = await ScoreLiquidity(await BuildAsync(), current: 0.68, acid: 0.35);

        Assert.Equal(["liquidez_corriente", "prueba_acida"], r.CriticalRatios);
        Assert.False(r.AcidTestFloorWaived);
        Assert.Equal("below", r.CurrentRatioVsWarn);
        Assert.Equal(60m, r.AppliedFloor);   // the liquidez floor, untouched, wins over 50
        Assert.Equal(60m, r.Score);
    }

    [Fact]
    public async Task CurrentRatioExactlyOnWarn_IsNotWeak_SoTheAcidFloorIsWaived()
    {
        decimal warn = ScoringDefaults.Get("threshold_liquidez_corriente_warn");
        var r = await ScoreLiquidity(await BuildAsync(), current: (double)warn, acid: 0.30);

        Assert.Equal(50m, Entry(r, "liquidez_corriente").Score);
        Assert.True(r.AcidTestFloorWaived);
        Assert.Equal("at_or_above", r.CurrentRatioVsWarn);
        Assert.Empty(r.CriticalRatios);
        Assert.Equal(0m, r.AppliedFloor);
    }

    [Fact]
    public async Task UnreadCurrentRatio_OnTheRenormalisePath_KeepsTheAcidFloor()
    {
        var r = await ScoreLiquidity(await BuildAsync(), current: null, acid: 0.30);

        Assert.False(r.RatiosLowCoverageFallback);
        Assert.Contains("liquidez_corriente", r.NonComputableRatios);
        Assert.Equal(["prueba_acida"], r.CriticalRatios);
        Assert.False(r.AcidTestFloorWaived);
        Assert.Null(r.CurrentRatioVsWarn);
        Assert.Equal(50m, r.AppliedFloor);
        Assert.Equal(50m, r.Score);
    }

    [Fact]
    public async Task UnreadCurrentRatio_OnTheLowCoverageFallback_KeepsTheAcidFloor()
    {
        // Only acid (0.10) and ROA (0.08) survive: 0.18 < 0.50, so nothing is excluded and the unread current
        // ratio scores the neutral 50 at full weight. It is still unread — no evidence of coverage.
        var ratios = new RatiosData { AcidTest = 0.30, Roa = 10.0 };
        var r = await Score(await BuildAsync(), ratios, BcraNoDebt(), MacroBenign());

        Assert.True(r.RatiosLowCoverageFallback);
        Assert.Equal(["prueba_acida"], r.CriticalRatios);
        Assert.False(r.AcidTestFloorWaived);
        Assert.Null(r.CurrentRatioVsWarn);
        Assert.Equal(50m, r.AppliedFloor);
    }

    [Fact]
    public async Task NoCurrentLiabilities_IsTheBestLiquidity_SoACriticalAcidTestIsWaived()
    {
        // A synthetic combination: the current ratio carries the no-current-liabilities marker (best band)
        // while an acid test value is present and at danger. The best liquidity there is is not weak.
        var ratios = Liquidity(current: null, acid: 0.30);
        ratios.ExclusionReasons = new Dictionary<string, string>
        {
            ["liquidez_corriente"] = RatioExclusionReasons.NoCurrentLiabilities,
        };

        var r = await Score(await BuildAsync(), ratios, BcraNoDebt(), MacroBenign());

        Assert.Equal("Critico", Entry(r, "prueba_acida").Status);
        Assert.True(r.AcidTestFloorWaived);
        Assert.Equal("at_or_above", r.CurrentRatioVsWarn);
        Assert.Empty(r.CriticalRatios);
        Assert.Equal(0m, r.AppliedFloor);
    }

    [Fact]
    public async Task AcidTestNotCritical_LeavesTheRuleUnconsulted()
    {
        var r = await ScoreLiquidity(await BuildAsync(), current: 1.94, acid: 0.80);

        Assert.False(r.AcidTestFloorWaived);
        Assert.Null(r.CurrentRatioVsWarn);
        Assert.Empty(r.CriticalRatios);
    }

    // ── Flag off: version 10 ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FlagOff_RestoresVersion10_WithTheTenantsStoredFloorOf60()
    {
        var r = await ScoreLiquidity(await BuildAsync((FlagKey, 0m), (FloorKey, 60m)), current: 1.94, acid: 0.50);

        Assert.Equal(["prueba_acida"], r.CriticalRatios);
        Assert.False(r.AcidTestFloorWaived);
        Assert.Null(r.CurrentRatioVsWarn);
        Assert.Equal(60m, r.AppliedFloor);
        Assert.Equal(60m, r.Score);
    }

    [Fact]
    public async Task FlagOn_WithAStoredRowOf60_AWeakCurrentRatioFloorsAt60_TheRowWins()
    {
        var r = await ScoreLiquidity(await BuildAsync((FloorKey, 60m)), current: 0.84, acid: 0.28);

        Assert.Equal(["prueba_acida"], r.CriticalRatios);
        Assert.Equal(60m, r.AppliedFloor);
    }

    // ── Sector overlay ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SectorWarnThreshold_DecidesWhetherTheCurrentRatioIsWeak()
    {
        // Agro raises its current-ratio thresholds: warn 2.00 (ok 2.50 so the triplet stays ordered and the
        // ordering fallback does not put the defaults back). 1.94 is weak for Agro, comfortable for General.
        var sut = await BuildAsync(
            ("threshold_liquidez_corriente_ok__sector__Agro",   2.50m),
            ("threshold_liquidez_corriente_warn__sector__Agro", 2.00m));

        var agro    = await ScoreLiquidity(sut, current: 1.94, acid: 0.50, sector: "Agro");
        var general = await ScoreLiquidity(sut, current: 1.94, acid: 0.50);

        Assert.Empty(agro.ThresholdFallbacks);
        Assert.Equal("Agro", agro.SectorProfile);
        Assert.Equal(["prueba_acida"], agro.CriticalRatios);
        Assert.False(agro.AcidTestFloorWaived);
        Assert.Equal("below", agro.CurrentRatioVsWarn);
        Assert.Equal(50m, agro.AppliedFloor);

        Assert.True(general.AcidTestFloorWaived);
        Assert.Equal("at_or_above", general.CurrentRatioVsWarn);
        Assert.Equal(0m, general.AppliedFloor);
    }

    // ── Ordering against the LUFE sales penalty ───────────────────────────────────────────────

    [Fact]
    public async Task LufeSalesPenalty_StillRunsAfterTheFloor()
    {
        var floored = await ScoreLiquidity(await BuildAsync(), current: 0.84, acid: 0.28,
            ventas: VentasInteranual(-0.50m));

        Assert.Equal(50m, floored.AppliedFloor);
        Assert.True(floored.VentasAdj > 0m);
        Assert.Equal(Math.Round(50m + floored.VentasAdj, 2), floored.Score);

        var waived = await ScoreLiquidity(await BuildAsync(), current: 1.94, acid: 0.50,
            ventas: VentasInteranual(-0.50m));

        Assert.True(waived.AcidTestFloorWaived);
        Assert.True(waived.VentasAdj > 0m);
        Assert.Equal(Math.Round(waived.ScoreBeforeFloor + waived.VentasAdj, 2), waived.Score);
    }

    // ── The persisted breakdown ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Breakdown_RecordsTheWaiver_AdditivelyBeforeLevelCutoffs()
    {
        var waived = await ScoreLiquidity(await BuildAsync(), current: 1.94, acid: 0.50);
        string json = AnalysisScoringStage.BuildScoreBreakdownJson(waived, null);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("acidTestFloorWaived").GetBoolean());
        Assert.Equal("at_or_above", root.GetProperty("currentRatioVsWarn").GetString());
        Assert.Equal(0, root.GetProperty("criticalRatios").GetArrayLength());
        Assert.Equal("Critico", root.GetProperty("breakdown").GetProperty("prueba_acida").GetProperty("status").GetString());
        Assert.Equal("11", root.GetProperty("scoringAlgoVersion").GetString());

        var names = root.EnumerateObject().Select(p => p.Name).ToList();
        int sign = names.IndexOf("operatingResultSign");
        Assert.Equal("acidTestFloorWaived", names[sign + 1]);
        Assert.Equal("currentRatioVsWarn",  names[sign + 2]);
        Assert.Equal("levelCutoffs",        names[sign + 3]);

        var unconsulted = await Score(await BuildAsync(), HealthyRatios(), BcraNoDebt(), MacroBenign());
        using var neutral = JsonDocument.Parse(AnalysisScoringStage.BuildScoreBreakdownJson(unconsulted, null));
        Assert.False(neutral.RootElement.GetProperty("acidTestFloorWaived").GetBoolean());
        Assert.Equal(JsonValueKind.Null, neutral.RootElement.GetProperty("currentRatioVsWarn").ValueKind);
    }

    // ── Chat, narrative and PDF wording ───────────────────────────────────────────────────────

    [Fact]
    public async Task Chat_ExplainsTheWaiver_OnlyWhenItHappened()
    {
        var waived = await ScoreLiquidity(await BuildAsync(), current: 1.94, acid: 0.50);
        string block = AnalysisController.FormatScoreBreakdownBlock(
            AnalysisScoringStage.BuildScoreBreakdownJson(waived, null), waived.Score)!;

        Assert.Contains(ScoringNoticeTexts.AcidTestFloorWaived, block);
        Assert.Contains("Prueba ácida sigue en «Critico»", block);
        Assert.DoesNotContain("se redujo a", block);

        var floored = await ScoreLiquidity(await BuildAsync(), current: 0.84, acid: 0.28);
        string flooredBlock = AnalysisController.FormatScoreBreakdownBlock(
            AnalysisScoringStage.BuildScoreBreakdownJson(floored, null), floored.Score)!;

        Assert.DoesNotContain(ScoringNoticeTexts.AcidTestFloorWaived, flooredBlock);
    }

    [Fact]
    public async Task Narrative_ExplainsTheWaiver_OnlyWhenItHappened()
    {
        var waivedRatios = Liquidity(current: 1.94, acid: 0.50);
        var waived = await Score(await BuildAsync(), waivedRatios, BcraNoDebt(), MacroBenign());
        var prompt = ClaudeService.BuildNarrativePrompt(waived, waivedRatios, null, null);

        Assert.Contains("PISO POR PRUEBA ÁCIDA NO APLICADO", prompt);
        Assert.Contains(ScoringNoticeTexts.AcidTestFloorWaived, prompt);
        Assert.DoesNotContain("AJUSTE POR RATIO CRÍTICO", prompt);

        var flooredRatios = Liquidity(current: 0.84, acid: 0.28);
        var floored = await Score(await BuildAsync(), flooredRatios, BcraNoDebt(), MacroBenign());
        var flooredPrompt = ClaudeService.BuildNarrativePrompt(floored, flooredRatios, null, null);

        Assert.DoesNotContain("PISO POR PRUEBA ÁCIDA NO APLICADO", flooredPrompt);
        Assert.DoesNotContain(ScoringNoticeTexts.AcidTestFloorWaived, flooredPrompt);
    }

    [Fact]
    public async Task Pdf_ExplainsTheWaiver_OnlyWhenItHappened()
    {
        var waivedRatios = Liquidity(current: 1.94, acid: 0.50);
        var waived = await Score(await BuildAsync(), waivedRatios, BcraNoDebt(), MacroBenign());
        string waivedText = await RenderPdfText(waived, waivedRatios);

        Assert.Contains(ScoringNoticeTexts.AcidTestFloorWaived, waivedText);

        var flooredRatios = Liquidity(current: 0.84, acid: 0.28);
        var floored = await Score(await BuildAsync(), flooredRatios, BcraNoDebt(), MacroBenign());
        string flooredText = await RenderPdfText(floored, flooredRatios);

        Assert.DoesNotContain(ScoringNoticeTexts.AcidTestFloorWaived.Split(':')[0], flooredText);
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
            ResultId           = new Guid("11111111-1111-1111-1111-111111111111"),
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
