// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Pdf;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfReport;

/// <summary>
/// The first page's "what moved the score after the composite" line: each composite-level adjustment with the
/// ENGINE's applied value, the rounding to the score scale on its own — never folded into an adjustment.
/// </summary>
public sealed class ScoreAdjustmentLineTests
{
    // Components 5 / 10 / 10 → 950 × 0,50 + 900 × 0,35 + 900 × 0,15 = 475 + 315 + 135 = 925.
    private static ScoreAnatomy.Input Components() => new()
    {
        ScoreRatios           = 5m,
        ScoreBcra             = 10m,
        ScoreMacro            = 10m,
        BcraExcluded          = false,
        EffectiveWeightRatios = 0.50m,
        EffectiveWeightBcra   = 0.35m,
        EffectiveWeightMacro  = 0.15m,
    };

    [Fact]
    public void Composite925_Lufe100_Final820_NeverAttributesTheRoundingToLufe()
    {
        // The bug a real export showed: composite 925, the engine took 10 internal points (−100) for LUFE, and
        // the final is 820. The −5 between them is rounding, not LUFE: the line reads −100 and redondeo −5.
        ScoreAnatomy.Model anatomy = ScoreAnatomy.Build(Components() with { VentasAdj = 10m }, aiSuggestedScore: 18m);

        ScoreAdjustmentLine.Line line = ScoreAdjustmentLine.Build(anatomy);

        Assert.Equal(925m, line.Composite);
        Assert.Equal(820, line.Final);
        Assert.Collection(line.Parts,
            part => { Assert.Equal("ventas declaradas (LUFE)", part.Label); Assert.Equal(-100m, part.DeltaNosis); Assert.False(part.IsRounding); },
            part => { Assert.Equal("redondeo", part.Label); Assert.Equal(-5m, part.DeltaNosis); Assert.True(part.IsRounding); });
        Assert.Equal(
            "Score compuesto 925 → ventas declaradas (LUFE) −100 · redondeo −5 → score final 820.",
            line.Text);
        Assert.DoesNotContain("105", line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithTheEnginesComposite_TheLufePenaltyAddsUpWithNoRounding()
    {
        // scoreBeforeFloor 8 → the engine's composite 920; LUFE −100 → 820 exactly.
        ScoreAnatomy.Model anatomy = ScoreAnatomy.Build(
            Components() with { ScoreBeforeFloor = 8m, VentasAdj = 10m }, aiSuggestedScore: 18m);

        ScoreAdjustmentLine.Line line = ScoreAdjustmentLine.Build(anatomy);

        Assert.Equal("Score compuesto 920 → ventas declaradas (LUFE) −100 → score final 820.", line.Text);
    }

    [Fact]
    public void FloorLufeAndCasfog_InEngineOrder_EachWithItsOwnDelta()
    {
        // Composite 48,5 → 510; a critical ratio floors it to 65 → 350 (−160); LUFE 5 → −50; CASFOG 7,3 → −73;
        // final 77,3 → 230; the +3 left is rounding.
        ScoreAnatomy.Model anatomy = ScoreAnatomy.Build(
            Components() with
            {
                ScoreRatios = 60m, ScoreBcra = 40m, ScoreMacro = 30m,
                ScoreBeforeFloor = 48.5m, AppliedFloor = 65m, CriticalRatioCount = 1,
                VentasAdj = 5m, CasfogMoraAdj = 7.3m,
            },
            aiSuggestedScore: 77.3m);

        ScoreAdjustmentLine.Line line = ScoreAdjustmentLine.Build(anatomy);

        Assert.Equal([-160m, -50m, -73m, 3m], line.Parts.Select(part => part.DeltaNosis));
        Assert.Equal(
            "Score compuesto 510 → piso por ratio crítico −160 · ventas declaradas (LUFE) −50 · " +
            "garantías SGR en mora (CASFOG) −73 · redondeo +3 → score final 230.",
            line.Text);
    }

    [Fact]
    public void NoAdjustments_SaysSo()
    {
        ScoreAnatomy.Model anatomy = ScoreAnatomy.Build(Components() with { ScoreBeforeFloor = 7.5m }, aiSuggestedScore: 7.5m);

        ScoreAdjustmentLine.Line line = ScoreAdjustmentLine.Build(anatomy);

        Assert.False(line.HasAdjustments);
        Assert.Equal("Score compuesto 920 → sin ajustes ni pisos → score final 920.", line.Text);
    }

    [Fact]
    public void AFloorThatDidNotBind_IsNotListedAsApplied()
    {
        ScoreAnatomy.Model anatomy = ScoreAnatomy.Build(
            Components() with
            {
                ScoreRatios = 60m, ScoreBcra = 40m, ScoreMacro = 30m,
                ScoreBeforeFloor = 48.5m, AppliedFloor = 40m, CriticalRatioCount = 1,
            },
            aiSuggestedScore: 48.5m);

        ScoreAdjustmentLine.Line line = ScoreAdjustmentLine.Build(anatomy);

        Assert.DoesNotContain("piso por ratio", line.Text, StringComparison.Ordinal);
        Assert.Contains("sin ajustes ni pisos", line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyRounding_IsStatedAfterSinAjustes()
    {
        // Legacy row (no scoreBeforeFloor): 925 of components, final 7,5 → round 8 → 920.
        ScoreAnatomy.Model anatomy = ScoreAnatomy.Build(Components(), aiSuggestedScore: 7.5m);

        ScoreAdjustmentLine.Line line = ScoreAdjustmentLine.Build(anatomy);

        Assert.Equal("Score compuesto 925 → sin ajustes ni pisos · redondeo −5 → score final 920.", line.Text);
    }
}
