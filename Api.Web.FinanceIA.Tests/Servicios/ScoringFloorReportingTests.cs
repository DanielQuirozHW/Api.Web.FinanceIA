// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-01

using System.Globalization;
using System.Text;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Pins the three places that tell an analyst the score was floored — the narrative prompt, the chat
/// system prompt and the PDF export — against the case where a ratio is critical but the floor did
/// NOT actually move the score.
/// <para>
/// All three used to branch on <c>AppliedFloor &gt; 0</c>, which is the floor COMPUTED from the
/// triggered ratios rather than one that bound, so all three announced a reduction that never
/// happened (finding D1). They now use ScoringService's own guard,
/// <c>ScoreBeforeFloor &lt; AppliedFloor</c>, recovered from the persisted breakdown.
/// </para>
/// <para>
/// GAP THIS FILLS: none of these three branches had a single test. The PDF fixtures carried no
/// <c>appliedFloor</c> key at all, so the callout was never rendered under test, and
/// <c>FormatScoreBreakdownBlock</c>'s floor branch was never reached.
/// </para>
/// <para>
/// Because the derivation reads two fields that are always persisted together, it is also correct
/// for rows written BEFORE the fix — which is why the fix is here and not in the engine: the blob is
/// never rewritten, so an engine-side change could not have repaired a single existing analysis.
/// </para>
/// </summary>
public sealed class ScoringFloorReportingTests
{
    private const string FloorSectionHeading = "AJUSTE POR RATIO CRÍTICO";

    // A ratio IS critical (so CriticalRatios is non-empty and a floor was computed), but the
    // composite already sits above that floor, so nothing was raised: Score == ScoreBeforeFloor.
    private static ScoringResult FloorTriggeredButNotBinding() => Scoring(
        score: 90.25m, scoreBeforeFloor: 90.25m, appliedFloor: 70.00m);

    // The floor bound: the composite was below it and got raised to it.
    private static ScoringResult FloorBinding() => Scoring(
        score: 65.00m, scoreBeforeFloor: 38.00m, appliedFloor: 65.00m);

    private static ScoringResult Scoring(decimal score, decimal scoreBeforeFloor, decimal appliedFloor) => new()
    {
        Score            = score,
        ScoreBeforeFloor = scoreBeforeFloor,
        AppliedFloor     = appliedFloor,
        Level            = "Critical",
        ScoreRatios      = 24m,
        ScoreBcra        = 50m,
        ScoreMacro       = 50m,
        CriticalRatios   = ["leverage"],
        Breakdown        = new Dictionary<string, RatioBreakdownEntry>
        {
            ["leverage"] = new RatioBreakdownEntry(85m, "Critico")
        },
        EffectiveWeightRatios = 0.50m,
        EffectiveWeightBcra   = 0.35m,
        EffectiveWeightMacro  = 0.15m
    };

    private static RatiosData Ratios() => new() { Leverage = 2.67 };

    // ── 1. The narrative prompt ─────────────────────────────────────────────────────────────────

    [Fact]
    public void NarrativePrompt_WhenFloorTriggeredButNotBinding_OmitsTheFloorSection()
    {
        var prompt = ClaudeService.BuildNarrativePrompt(
            FloorTriggeredButNotBinding(), Ratios(), null, null);

        Assert.DoesNotContain(FloorSectionHeading, prompt);
        // The specific sentence that was false: the score never moved, so nothing "se redujo".
        Assert.DoesNotContain("se redujo", prompt);
    }

    [Fact]
    public void NarrativePrompt_WhenFloorBinds_KeepsTheFloorSection()
    {
        var prompt = ClaudeService.BuildNarrativePrompt(FloorBinding(), Ratios(), null, null);

        Assert.Contains(FloorSectionHeading, prompt);
        Assert.Contains("se redujo", prompt);
    }

    // The neutral result CalculateAsync returns from its catch-all carries Score 50 with
    // ScoreBeforeFloor and AppliedFloor both 0 and no critical ratios — scoring never ran. That
    // result still reaches BuildNarrativePrompt, and nothing covered it: a floor claim on an
    // analysis the engine failed to score would be the worst version of this defect.
    [Fact]
    public void NarrativePrompt_ForTheNeutralFallbackResult_MakesNoFloorClaim()
    {
        var neutral = new ScoringResult
        {
            Score = 50m, Level = "Medium",
            ScoreRatios = 50m, ScoreBcra = 50m, ScoreMacro = 50m,
            SectorProfile = "General"
            // ScoreBeforeFloor, AppliedFloor and CriticalRatios stay at their defaults, as the
            // engine's catch-all leaves them.
        };

        var prompt = ClaudeService.BuildNarrativePrompt(neutral, Ratios(), null, null);

        Assert.DoesNotContain(FloorSectionHeading, prompt);
        Assert.DoesNotContain("se redujo", prompt);
    }

    // ── 2. The chat system prompt ───────────────────────────────────────────────────────────────
    //
    // Rebuilt and re-sent on EVERY turn of every conversation, so a false statement here is repeated
    // for the whole life of the thread. Fed from the persisted blob, not a live ScoringResult.

    // Built with InvariantCulture on purpose: a comma-decimal test host would otherwise emit
    // "90,25" and produce a blob that does not parse, turning these into false passes.
    private static string Blob(decimal scoreBeforeFloor, decimal appliedFloor)
    {
        string before = scoreBeforeFloor.ToString(CultureInfo.InvariantCulture);
        string floor  = appliedFloor.ToString(CultureInfo.InvariantCulture);
        return $$"""
        {
          "scoreRatios": 24, "scoreBcra": 50, "scoreMacro": 50,
          "scoreBeforeFloor": {{before}}, "appliedFloor": {{floor}},
          "criticalRatios": ["leverage"]
        }
        """;
    }

    [Fact]
    public void ChatBreakdownBlock_WhenFloorTriggeredButNotBinding_OmitsTheFloorLine()
    {
        string block = AnalysisController.FormatScoreBreakdownBlock(
            Blob(scoreBeforeFloor: 90.25m, appliedFloor: 70.00m), finalScore: 90.25m)
            ?? throw new Xunit.Sdk.XunitException("the breakdown block must render");

        Assert.DoesNotContain("se redujo", block);
    }

    [Fact]
    public void ChatBreakdownBlock_WhenFloorBinds_KeepsTheFloorLine()
    {
        string block = AnalysisController.FormatScoreBreakdownBlock(
            Blob(scoreBeforeFloor: 38m, appliedFloor: 65m), finalScore: 65m)
            ?? throw new Xunit.Sdk.XunitException("the breakdown block must render");

        Assert.Contains("se redujo", block);
    }

    // A row persisted before the floor feature has neither key; the lifted comparison on two nulls
    // is false, so it must stay silent rather than throw or emit a half-built sentence.
    [Fact]
    public void ChatBreakdownBlock_WhenBlobPredatesTheFloorFeature_OmitsTheFloorLine()
    {
        var block = AnalysisController.FormatScoreBreakdownBlock(
            """{ "scoreRatios": 24, "scoreBcra": 50, "scoreMacro": 50 }""", finalScore: 42m);

        Assert.DoesNotContain("se redujo", block ?? string.Empty);
    }

    // ── 2b. The legacy blob shape: appliedFloor present, scoreBeforeFloor absent ────────────────
    //
    // The derivation needs both keys. When the before-score is missing we cannot tell whether the
    // floor bound, and staying silent would DROP the callout from rows where it genuinely did — a
    // narrowing, not a repair. So an absent before-score falls back to the old, more permissive
    // `AppliedFloor > 0`. These two tests are what stop the fallback being "simplified" away; the
    // "n/d" arm in the rendered sentence exists for this same shape.

    [Fact]
    public void ChatBreakdownBlock_WhenBeforeScoreIsAbsent_StillRendersTheFloorLine()
    {
        string block = AnalysisController.FormatScoreBreakdownBlock(
            """{ "scoreRatios": 24, "criticalRatios": ["leverage"], "appliedFloor": 65.0 }""",
            finalScore: 65m)
            ?? throw new Xunit.Sdk.XunitException("the breakdown block must render");

        // The notice survives, and the unknown before-score is reported as unknown rather than
        // invented — the exact sentence, so this cannot pass on an "n/d" from some other line.
        Assert.Contains("era n/d, y se redujo a", block);
    }

    [Fact]
    public async Task PdfRiskSection_WhenBeforeScoreIsAbsent_StillRendersTheCallout()
    {
        string text = await RenderRiskSectionText(
            """{ "scoreRatios": 24, "criticalRatios": ["leverage"], "appliedFloor": 65.0 }""",
            aiScore: 65m);

        Assert.Contains("se redujo", text);
    }

    // ── 3. The PDF export ───────────────────────────────────────────────────────────────────────

    private static async Task<string> RenderRiskSectionText(string scoreBreakdownJson, decimal aiScore)
    {
        var service = new PdfExportService(
            new CashFlowNarrativeService(),
            new CashFlowReconciliationService(),
            new AlertRatioReconciliationService(),
            NullLogger<PdfExportService>.Instance);

        var result = new AnalysisResult
        {
            ResultId           = new Guid("33333333-3333-3333-3333-333333333333"),
            AiSuggestedScore   = aiScore,
            ScoreBreakdownJson = scoreBreakdownJson,
        };

        byte[] pdf = await service.GenerateAsync(
            result, new Analysis { FiscalYear = 2025 }, "tenant-x",
            new[] { "risk" }, CancellationToken.None);

        using var reader = new PdfReader(new MemoryStream(pdf));
        using var doc = new PdfDocument(reader);
        var sb = new StringBuilder();
        for (int i = 1; i <= doc.GetNumberOfPages(); i++)
            sb.AppendLine(PdfTextExtractor.GetTextFromPage(doc.GetPage(i)));
        return sb.ToString();
    }

    [Fact]
    public async Task PdfRiskSection_WhenFloorTriggeredButNotBinding_OmitsTheCallout()
    {
        string text = await RenderRiskSectionText(
            Blob(scoreBeforeFloor: 90.25m, appliedFloor: 70.00m), aiScore: 90.25m);

        Assert.DoesNotContain("se redujo", text);
    }

    [Fact]
    public async Task PdfRiskSection_WhenFloorBinds_RendersTheCallout()
    {
        string text = await RenderRiskSectionText(
            Blob(scoreBeforeFloor: 38m, appliedFloor: 65m), aiScore: 65m);

        Assert.Contains("se redujo", text);
        // The floor branch is the only place the critical ratio's raw key reaches the PDF.
        Assert.Contains("leverage", text);
    }

    // ── 4. Explaining the floor (scoring algorithm 9) ───────────────────────────────────────────
    //
    // Since version 9 the blob records the trigger and every ratio's floor, so the chat can say WHY a
    // ratio is (or is not) critical and what ceiling that imposes, instead of sending the analyst to
    // the risk team. All figures are on the NOSIS scale the rest of the block uses: a risk floor of 65
    // is a ceiling of 350/1000.

    private const string FloorsV9 =
        "\"criticalFloors\": { \"liquidez_corriente\": 60, \"prueba_acida\": 60, \"endeudamiento\": 65, " +
        "\"leverage\": 65, \"cobertura_intereses\": 70, \"roe\": 55, \"roa\": 55, \"margen_neto\": 55 }";

    private static string BlobV9(decimal trigger, bool floorBound) => $$"""
        {
          "scoreRatios": 24, "scoreBcra": 50, "scoreMacro": 50,
          "scoreBeforeFloor": {{(floorBound ? "38" : "15.87")}}, "appliedFloor": {{(floorBound ? "65" : "0")}},
          "criticalRatios": {{(floorBound ? "[\"leverage\"]" : "[]")}},
          "criticalFloorTrigger": {{trigger.ToString(CultureInfo.InvariantCulture)}},
          {{FloorsV9}}
        }
        """;

    [Fact]
    public void ChatBreakdownBlock_WithTheV9Keys_ExplainsTheTriggerAndEveryFloor_EvenWhenNoneBound()
    {
        string block = AnalysisController.FormatScoreBreakdownBlock(
            BlobV9(100m, floorBound: false), finalScore: 15.87m)
            ?? throw new Xunit.Sdk.XunitException("the breakdown block must render");

        Assert.Contains(
            "Piso por ratio crítico: un ratio es crítico —estado «Critico» en los scores parciales de arriba— " +
            "sólo cuando alcanza o supera su umbral de PELIGRO", block);
        Assert.Contains("el score final queda como máximo en el tope de ese ratio, en la escala /1000", block);
        Assert.DoesNotContain("score de riesgo", block);   // the ceiling is stated on /1000 only
        Assert.Contains("Apalancamiento 350/1000", block);
        Assert.Contains("Endeudamiento 350/1000", block);
        Assert.Contains("Cobertura de intereses 300/1000", block);
        Assert.Contains("ROE 450/1000", block);
        Assert.Contains("Liquidez corriente 400/1000", block);
        Assert.DoesNotContain("se redujo", block);   // nothing bound, so no reduction is claimed
    }

    // A tenant that still configures the pre-v9 trigger must not be told "at DANGER": its ratios go
    // critical earlier, and the sentence has to say so. It is tied to the «Critico» label rather than to
    // the trigger as a /1000 number, which has the same rounding edge the 100 case has (see below).
    [Fact]
    public void ChatBreakdownBlock_WithATenantTriggerBelow100_SaysItFiresBeforeDanger()
    {
        string block = AnalysisController.FormatScoreBreakdownBlock(
            BlobV9(85m, floorBound: true), finalScore: 65m)!;

        Assert.Contains(
            "cuando alcanza el umbral de disparo configurado por la entidad, que se activa antes de su umbral de PELIGRO",
            block);
        Assert.Contains("estado «Critico»", block);
        Assert.DoesNotContain("alcanza o supera su umbral de PELIGRO", block);
        Assert.Contains("se redujo", block);
    }

    // The rounding edge. Leverage 5.65 is short of DANGER (5.67): its partial is
    // 50 + (5.65-2.33)/(5.67-2.33)*50 = 99.70, "Atencion", not critical, no floor — but ToNosis rounds
    // 99.70 to 100, so the per-ratio line reads "Apalancamiento: 0/1000 — Atencion". The rule must not
    // say "critical at 0/1000" next to that line; it names the «Critico» label and says outright that an
    // «Atencion» ratio floors nothing even at a rounded 0/1000. Scored by the real engine and persisted
    // with the keys the controller writes.
    [Fact]
    public async Task ChatBreakdownBlock_LeverageJustShortOfDanger_DoesNotContradictTheRule()
    {
        var sut     = await ScoringTestHarness.BuildAsync();
        var ratios  = ScoringTestHarness.HealthyRatios();
        ratios.Leverage = 5.65;

        var r = await ScoringTestHarness.Score(
            sut, ratios, ScoringTestHarness.BcraNoDebt(), ScoringTestHarness.MacroBenign());

        Assert.Equal(99.70m, r.Breakdown["leverage"].Score);
        Assert.Equal("Atencion", r.Breakdown["leverage"].Status);
        Assert.Empty(r.CriticalRatios);

        string json = System.Text.Json.JsonSerializer.Serialize(new
        {
            scoreRatios          = r.ScoreRatios,
            scoreBcra            = r.ScoreBcra,
            scoreMacro           = r.ScoreMacro,
            breakdown            = r.Breakdown,
            scoreBeforeFloor     = r.ScoreBeforeFloor,
            appliedFloor         = r.AppliedFloor,
            criticalRatios       = r.CriticalRatios,
            criticalFloorTrigger = r.CriticalFloorTrigger,
            criticalFloors       = r.CriticalFloors,
        });

        string block = AnalysisController.FormatScoreBreakdownBlock(json, r.Score)!;

        Assert.Contains("Apalancamiento: 0/1000 — Atencion", block);   // the rounding edge is really there
        Assert.DoesNotContain("score parcial 0/1000", block);
        Assert.DoesNotContain("0/1000)", block);
        Assert.Contains("un ratio es crítico —estado «Critico» en los scores parciales de arriba—", block);
        Assert.Contains("Un ratio en «Atencion» no activa el piso, aunque su score parcial redondeado llegue a 0/1000.", block);
        Assert.DoesNotContain("se redujo", block);
    }

    // A row scored before version 9 has no such keys. When its floor bound, the mechanism is still
    // explained — without numbers, because the ones it used were never recorded.
    [Fact]
    public void ChatBreakdownBlock_ForAPreV9RowWhoseFloorBound_ExplainsTheMechanismWithoutNumbers()
    {
        string block = AnalysisController.FormatScoreBreakdownBlock(
            Blob(scoreBeforeFloor: 38m, appliedFloor: 65m), finalScore: 65m)!;

        Assert.Contains("no quedaron registrados", block);
        Assert.DoesNotContain("en vigor al calificar:", block);
    }

    [Fact]
    public void ChatBreakdownBlock_ForAPreV9RowWithNoFloor_AddsNothing()
    {
        string block = AnalysisController.FormatScoreBreakdownBlock(
            Blob(scoreBeforeFloor: 90.25m, appliedFloor: 70.00m), finalScore: 90.25m)!;

        Assert.DoesNotContain("Piso por ratio crítico", block);
    }

    [Fact]
    public void NarrativePrompt_WhenFloorBinds_StatesTheRuleAndTheCeiling()
    {
        var scoring = FloorBinding();
        scoring.CriticalFloorTrigger = 100m;

        var prompt = ClaudeService.BuildNarrativePrompt(scoring, Ratios(), null, null);

        Assert.Contains("Un ratio es crítico cuando alcanza o supera su umbral de peligro.", prompt);
        Assert.Contains("El piso fija un máximo de 350/1000 para el score final.", prompt);
    }

    [Fact]
    public void NarrativePrompt_WithATenantTriggerBelow100_StatesItsOwnTrigger()
    {
        var scoring = FloorBinding();
        scoring.CriticalFloorTrigger = 85m;

        var prompt = ClaudeService.BuildNarrativePrompt(scoring, Ratios(), null, null);

        Assert.Contains("su score parcial llega a 150/1000 o menos", prompt);
        Assert.DoesNotContain("alcanza o supera su umbral de peligro", prompt);
    }

    // ── 5. A failed scoring never records a trigger ─────────────────────────────────────────────
    //
    // When CalculateAsync throws, it returns the neutral result without running the engine. With a
    // plain decimal trigger that result carried 0 — persisted as "criticalFloorTrigger": 0, which reads
    // as "every ratio is critical". It is now null, the floors are empty, and nothing downstream states
    // a rule. The fixture is the catch-all's own: two rows colliding on (TenantId, ConfigKey) make
    // LoadConfigAsync's ToDictionary throw.

    [Fact]
    public async Task NeutralFallback_RecordsNoTrigger_AndTheChatStatesNoFloorRule()
    {
        var sut = await ScoringTestHarness.BuildFromRowsAsync(
            ScoringTestHarness.Row(ScoringTestHarness.Tenant, "weight_component_bcra", 0.35m),
            ScoringTestHarness.Row(ScoringTestHarness.Tenant, "weight_component_bcra", 0.40m));

        var r = await ScoringTestHarness.Score(
            sut, ScoringTestHarness.BaselineRatios(), ScoringTestHarness.BcraNoDebt(), ScoringTestHarness.MacroBenign());

        Assert.Null(r.ScoringAlgoVersion);   // proves the catch path: the engine never ran
        Assert.Null(r.CriticalFloorTrigger);
        Assert.Empty(r.CriticalFloors);
        Assert.Empty(r.ThresholdFallbacks);

        // Persisted with the keys the controller writes: the trigger is null, never 0.
        string json = System.Text.Json.JsonSerializer.Serialize(new
        {
            scoreRatios          = r.ScoreRatios,
            scoreBcra            = r.ScoreBcra,
            scoreMacro           = r.ScoreMacro,
            breakdown            = r.Breakdown,
            scoreBeforeFloor     = r.ScoreBeforeFloor,
            appliedFloor         = r.AppliedFloor,
            criticalRatios       = r.CriticalRatios,
            criticalFloorTrigger = r.CriticalFloorTrigger,
            criticalFloors       = r.CriticalFloors,
            thresholdFallbacks   = r.ThresholdFallbacks,
        });

        Assert.Contains("\"criticalFloorTrigger\":null", json);
        Assert.DoesNotContain("\"criticalFloorTrigger\":0", json);

        string block = AnalysisController.FormatScoreBreakdownBlock(json, r.Score)!;

        Assert.DoesNotContain("Piso por ratio crítico", block);
    }

    // The narrative prompt never sees a null trigger with a bound floor in practice (the neutral result
    // binds none), but if it did it must state no rule rather than a wrong one.
    [Fact]
    public void NarrativePrompt_WithANullTrigger_StatesNoRule()
    {
        var scoring = FloorBinding();
        scoring.CriticalFloorTrigger = null;

        var prompt = ClaudeService.BuildNarrativePrompt(scoring, Ratios(), null, null);

        Assert.Contains("AJUSTE POR RATIO CRÍTICO", prompt);
        Assert.DoesNotContain("Un ratio es crítico cuando", prompt);
        Assert.DoesNotContain("0/1000 o menos", prompt);
    }
}
