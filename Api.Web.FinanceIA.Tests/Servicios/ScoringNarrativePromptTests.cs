// Project : Api.Web.FinanceIA.Tests
// Created : 2026-06-17

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

public sealed class ScoringNarrativePromptTests
{
    private static ScoringResult MakeScoringResult(
        decimal score,
        decimal scoreBeforeFloor,
        decimal appliedFloor,
        List<string>? criticalRatios = null) => new()
    {
        Score            = score,
        ScoreBeforeFloor = scoreBeforeFloor,
        AppliedFloor     = appliedFloor,
        Level            = "Critical",
        ScoreRatios      = 24m,
        ScoreBcra        = 50m,
        ScoreMacro       = 50m,
        CriticalRatios   = criticalRatios ?? new List<string>(),
        Breakdown        = new Dictionary<string, RatioBreakdownEntry>
        {
            ["leverage"] = new RatioBreakdownEntry(85m, "Critico")
        },
        EffectiveWeightRatios = 0.50m,
        EffectiveWeightBcra   = 0.35m,
        EffectiveWeightMacro  = 0.15m
    };

    private static RatiosData MakeRatios() => new()
    {
        Leverage = 2.67
    };

    [Fact]
    public void BuildNarrativePrompt_WithFloor_ContainsFloorSection()
    {
        var scoring = MakeScoringResult(
            score: 65m, scoreBeforeFloor: 38m, appliedFloor: 65m,
            criticalRatios: ["leverage"]);

        var prompt = ClaudeService.BuildNarrativePrompt(scoring, MakeRatios(), null, null);

        Assert.Contains("AJUSTE POR RATIO CRÍTICO", prompt);
        // The critical ratio is named with the report's table label, not the raw key.
        Assert.Contains("Apalancamiento", prompt);
        Assert.DoesNotContain("leverage", prompt);
    }

    [Fact]
    public void BuildNarrativePrompt_WithoutFloor_NoFloorSection()
    {
        var scoring = MakeScoringResult(
            score: 14m, scoreBeforeFloor: 14m, appliedFloor: 0m);

        var prompt = ClaudeService.BuildNarrativePrompt(scoring, MakeRatios(), null, null);

        Assert.DoesNotContain("AJUSTE POR RATIO CRÍTICO", prompt);
    }

    [Fact]
    public void BuildNarrativePrompt_WithFloor_MentionsBeforeAndAfterScore()
    {
        // ScoreBeforeFloor=38 → NOSIS (100-38)*10=620; Score=65 → NOSIS (100-65)*10=350
        var scoring = MakeScoringResult(
            score: 65m, scoreBeforeFloor: 38m, appliedFloor: 65m,
            criticalRatios: ["leverage"]);

        var prompt = ClaudeService.BuildNarrativePrompt(scoring, MakeRatios(), null, null);

        Assert.Contains("620/1000", prompt);  // score before floor
        Assert.Contains("350/1000", prompt);  // score after floor
    }

    [Fact]
    public void BuildNarrativePrompt_FormatsNumbersInEsAr()
    {
        var scoring = MakeScoringResult(score: 14m, scoreBeforeFloor: 14m, appliedFloor: 0m);

        var prompt = ClaudeService.BuildNarrativePrompt(scoring, MakeRatios(), null, null);

        // es-AR uses a comma decimal separator; the en-US "2.67" must not survive.
        Assert.Contains("2,67", prompt);
        Assert.DoesNotContain("2.67", prompt);
    }

    [Fact]
    public void BuildNarrativePrompt_UsesReportTableIndicatorLabels()
    {
        var scoring = MakeScoringResult(score: 14m, scoreBeforeFloor: 14m, appliedFloor: 0m);

        var prompt = ClaudeService.BuildNarrativePrompt(scoring, MakeRatios(), null, null);

        // Indicator names must match the PDF/Portal tables (RatioMetaMap), not the old vocabulary.
        Assert.Contains("Apalancamiento", prompt);
        Assert.Contains("Cobertura de intereses", prompt);
        Assert.DoesNotContain("Leverage", prompt);
    }

    [Fact]
    public void BuildNarrativePrompt_ExcludedRatio_NotDescribedAsGraded()
    {
        var scoring = MakeScoringResult(score: 14m, scoreBeforeFloor: 14m, appliedFloor: 0m);
        scoring.Breakdown = new Dictionary<string, RatioBreakdownEntry>
        {
            // Same status token ("Atencion") on both entries under DIFFERENT keys, so the test
            // cannot pass by accident: the non-excluded entry must still render normally.
            ["cobertura_intereses"] = new RatioBreakdownEntry(50m, "Atencion", Excluded: true, EffectiveWeight: 0m),
            ["leverage"]            = new RatioBreakdownEntry(72m, "Atencion")
        };

        var prompt = ClaudeService.BuildNarrativePrompt(scoring, MakeRatios(), null, null);

        Assert.DoesNotContain("Cobertura de intereses: Atencion", prompt);
        Assert.Contains("Cobertura de intereses: no medido", prompt);
        Assert.Contains("NO MEDIDOS", prompt);
        Assert.Contains("un ratio excluido/no medido no lleva ninguna de esas calificaciones", prompt);
        // Regression guard: a non-excluded ratio with the SAME status token still renders normally.
        Assert.Contains("Apalancamiento: Atencion", prompt);
    }

    [Fact]
    public void BuildNarrativePrompt_NoExcludedRatios_OmitsExclusionRule()
    {
        var scoring = MakeScoringResult(score: 14m, scoreBeforeFloor: 14m, appliedFloor: 0m);

        var prompt = ClaudeService.BuildNarrativePrompt(scoring, MakeRatios(), null, null);

        // The conditional REGLAS ESTRICTAS bullet (gated on excludedRatioNames.Count > 0) must be
        // absent entirely when nothing is excluded. Note the static IMPORTANTE carve-out sentence
        // ("...excluido/no medido no lleva ninguna de esas calificaciones") is unconditional by
        // design and always present, so it is intentionally not asserted against here.
        Assert.DoesNotContain("NO MEDIDOS", prompt);
        Assert.DoesNotContain("quedaron excluidos del score", prompt);
        // The breakdown-loop not-measured wording is per-entry and must not appear either.
        Assert.DoesNotContain("no medido (no pudo calcularse", prompt);
    }
}
