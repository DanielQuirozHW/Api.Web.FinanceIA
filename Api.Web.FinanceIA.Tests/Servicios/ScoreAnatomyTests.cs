// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-28

using Api.Web.FinanceIA.Servicios;
using Xunit;
using static Api.Web.FinanceIA.Servicios.ScoreAnatomy;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// <see cref="ScoreAnatomy"/> is a port of the Portal's <c>lib/score-anatomy.ts</c>
/// (PortalesHWA/Portal-FinanceIA#327). These are that file's <c>score-anatomy.test.ts</c> cases 1:1 —
/// same inputs, same expected outputs — so the exported PDF and the screen cannot drift apart
/// silently, updated to PortalesHWA/Portal-FinanceIA#429 (the engine's composite, each adjustment its own
/// engine delta, the rounding on its own line). Deviations are named where they occur: C# decimal has no NaN,
/// a negative delta is formatted with U+2212 where the Portal prints an ASCII hyphen, and a row without
/// scoreBeforeFloor does not fold its residual into the last adjustment either (the report never does).
/// </summary>
public sealed class ScoreAnatomyTests
{
    // The analysis the bug was reported on: 390 + 350 + 105, LUFE +1,6 internal, final 830.
    private static readonly Input RutaSur = Breakdown() with
    {
        ScoreRatios           = 22m,
        ScoreBcra             = 0m,
        ScoreMacro            = 30m,
        VentasAdj             = 1.6m,
        VentasVariacion       = -0.14m,
        VentasVariacionEsReal = true,
        VentasBase            = "interanual_3m",
    };

    // Composite internal 30 + 14 + 4.5 = 48.5 → 515 displayed; a critical ratio floors it to 65.
    private static readonly Input Floored = Breakdown() with
    {
        ScoreRatios        = 60m,
        ScoreBcra          = 40m,
        ScoreMacro         = 30m,
        ScoreBeforeFloor   = 48.5m,
        AppliedFloor       = 65m,
        CriticalRatioCount = 1,
    };

    private static Input Breakdown() => new()
    {
        ScoreRatios           = 20m,
        ScoreBcra             = 0m,
        ScoreMacro            = 20m,
        BcraExcluded          = false,
        EffectiveWeightRatios = 0.50m,
        EffectiveWeightBcra   = 0.35m,
        EffectiveWeightMacro  = 0.15m,
    };

    // The printed lines, summed the way an analyst reads them.
    private static decimal LinesTotal(Model model) =>
        JsRound((model.CompositeDisplay
                 + model.Adjustments.Sum(adjustment => adjustment.DeltaNosis)
                 + (model.RoundingLine ?? 0m)) * 10m) / 10m;

    // ── the RUTA SUR case ─────────────────────────────────────────────────────

    [Fact]
    public void RutaSur_KeepsTheContributionsExactlyAsDisplayedToday()
    {
        Model model = Build(RutaSur, 17.1m);

        Assert.Equal([390m, 350m, 105m], model.Contributions.Select(part => part.Value));
        Assert.Equal([780, 1000, 700], model.Contributions.Select(part => part.Nosis));
    }

    [Fact]
    public void RutaSur_ShowsTheCompositeAsTheSumOfThoseContributions()
    {
        Assert.Equal(845m, Build(RutaSur, 17.1m).CompositeDisplay);
    }

    [Fact]
    public void RutaSur_RestatesLufeAsANegative1000ScalePenaltyThatLandsOnTheGaugeValue()
    {
        Model model = Build(RutaSur, 17.1m);

        Adjustment ventas = Assert.Single(model.Adjustments);
        Assert.Equal(AdjustmentKey.Ventas, ventas.Key);
        Assert.Equal("Ventas declaradas (LUFE) — caída 14,0 % (interanual, 3 meses, real, deflactado por IPC)", ventas.Label);
        Assert.Equal("Ventas declaradas (LUFE)", ventas.ShortLabel);
        // The ENGINE's own delta: 1,6 internal is −16. The +1 of toNosis rounding is NOT folded into it any
        // more (Portal #429; the PDF never folds, not even on a row without scoreBeforeFloor): own line.
        Assert.Equal(-16m, ventas.DeltaNosis);
        Assert.Equal(830, model.FinalDisplay);
        Assert.Equal(1m, model.Rounding);
        Assert.Equal(1m, model.RoundingLine);
    }

    [Fact]
    public void RutaSur_AddsUp()
    {
        Model model = Build(RutaSur, 17.1m);

        Assert.Equal(model.FinalDisplay, LinesTotal(model));
    }

    // ── critical floor ────────────────────────────────────────────────────────

    [Fact]
    public void Floor_IsTheFirstAdjustment_OnThe1000Scale()
    {
        Model model = Build(Floored, 65m);

        // Portal #429: the composite is the ENGINE's, toNosis(scoreBeforeFloor 48,5) = 510, and the
        // components' 515 differs from it by a rounding of its own.
        Assert.Equal(510m, model.CompositeDisplay);
        Assert.Equal(-5m, model.ComponentsRoundingLine);
        Assert.True(model.ShowFloor);
        Adjustment floor = Assert.Single(model.Adjustments);
        Assert.Equal(AdjustmentKey.Floor, floor.Key);
        Assert.Equal("Piso por ratio crítico aplicado", floor.Label);
        Assert.Equal(-160m, floor.DeltaNosis);   // the floor figure 350 − the composite 510
        Assert.Equal(350m, model.AfterFloorDisplay);
        Assert.Equal(350, model.FinalDisplay);
        Assert.Equal(350m, LinesTotal(model));
    }

    [Fact]
    public void FloorLufeAndCasfog_InEngineOrder_TheResidualOnCasfog()
    {
        Model model = Build(
            Floored with { VentasAdj = 5m, VentasVariacion = -0.3m, CasfogMoraAdj = 7.3m, CasfogProporcionMora = 0.4m },
            65m + 5m + 7.3m);

        Assert.Equal(
            [AdjustmentKey.Floor, AdjustmentKey.Ventas, AdjustmentKey.Casfog],
            model.Adjustments.Select(adjustment => adjustment.Key));
        // Every adjustment keeps the engine's own delta (CASFOG 7,3 internal is −73); the +3 to land on 230
        // is the rounding line, never folded into CASFOG.
        Assert.Equal([-160m, -50m, -73m], model.Adjustments.Select(adjustment => adjustment.DeltaNosis));
        Assert.Equal(3m, model.Rounding);
        Assert.Equal(3m, model.RoundingLine);
        Assert.Equal(350m, model.AfterFloorDisplay);    // the callouts' "se redujo a"
        Assert.Equal(230, model.FinalDisplay);
        Assert.Equal(230m, LinesTotal(model));
    }

    [Fact]
    public void AReportedFloorTheCompositeAlreadyCleared_MovesNothing()
    {
        Model model = Build(Floored with { AppliedFloor = 40m }, 48.5m);

        Assert.Equal(AdjustmentKey.Floor, model.Adjustments[0].Key);
        Assert.Equal(0m, model.FloorRaised);
        // toNosis(48.5) = (100 − 49) × 10 = 510: the engine's composite IS the final, so nothing moved and
        // there is no residual to state.
        Assert.Equal(510, model.FinalDisplay);
        Assert.Equal(510m, model.CompositeDisplay);
        Assert.Equal(0m, model.Adjustments[0].DeltaNosis);
        Assert.Null(model.RoundingLine);
        Assert.Equal(510m, LinesTotal(model));
    }

    [Fact]
    public void NoFloorWithoutACriticalRatio()
    {
        Model model = Build(Floored with { CriticalRatioCount = 0 }, 48.5m);

        Assert.False(model.ShowFloor);
        Assert.Empty(model.Adjustments);
    }

    // ── rounding without adjustments ──────────────────────────────────────────

    [Fact]
    public void Rounding_SurfacesTheResidualOnItsOwnLine()
    {
        // 22/0/30 → 11 + 0 + 4.5 = 15.5 internal → toNosis 840; displayed contributions sum to 845.
        Model model = Build(Breakdown() with { ScoreRatios = 22m, ScoreMacro = 30m }, 15.5m);

        Assert.Equal(845m, model.CompositeDisplay);
        Assert.Equal(840, model.FinalDisplay);
        Assert.Equal(-5m, model.RoundingLine);
        Assert.Equal(840m, LinesTotal(model));
    }

    [Fact]
    public void Rounding_PrintsNoLineWhenThereIsNothingToRound()
    {
        // 20/0/20 → 10 + 0 + 3 = 13 internal → 870 = 400 + 350 + 120.
        Model model = Build(Breakdown(), 13m);

        Assert.Equal(870m, model.CompositeDisplay);
        Assert.Equal(0m, model.Rounding);
        Assert.Null(model.RoundingLine);
    }

    // ── sign ──────────────────────────────────────────────────────────────────

    [Fact]
    public void AnImprovement_NegativeInternalAdjustment_IsAPositiveDelta()
    {
        Model model = Build(Breakdown() with { CasfogMoraAdj = -5m }, 8m);

        Adjustment casfog = Assert.Single(model.Adjustments);
        Assert.Equal(AdjustmentKey.Casfog, casfog.Key);
        Assert.Equal(50m, casfog.DeltaNosis);
        Assert.Equal(920, model.FinalDisplay);
        Assert.Equal(920m, LinesTotal(model));
    }

    [Fact]
    public void NeverLeavesANegativeZero()
    {
        Model model = Build(Breakdown() with { VentasAdj = 0.01m }, 13.01m);

        Assert.Equal(0m, model.Adjustments[0].DeltaNosis);
        Assert.Equal("0", FormatDelta(model.Adjustments[0].DeltaNosis));
    }

    // ── cheques is inside the BCRA component ──────────────────────────────────

    [Fact]
    public void Cheques_IsAnInformationalBcraNote_AndNeverPartOfCompositeToFinal()
    {
        Model model = Build(Breakdown() with { ChequesAdj = 14.6m, ChequesProporcionRecuperada = 0.027m }, 13m);

        Note note = Assert.Single(model.BcraNotes);
        Assert.Equal("Cheques rechazados — importe recuperado 2,7 %", note.Label);
        Assert.Equal("Cheques rechazados", note.ShortLabel);
        Assert.Equal(-146m, note.DeltaNosis);
        Assert.Empty(model.Adjustments);
        Assert.Null(model.RoundingLine);
        Assert.Equal(model.FinalDisplay, LinesTotal(model));
    }

    // ── absent or zero signals ────────────────────────────────────────────────

    [Fact]
    public void AddsNothingForNullOrZeroAdjustments()
    {
        // The Portal case also passes NaN for casfogMoraAdj; decimal has no NaN, so null stands in.
        Model model = Build(Breakdown() with { ChequesAdj = 0m, VentasAdj = null, CasfogMoraAdj = null }, 13m);

        Assert.Empty(model.BcraNotes);
        Assert.Empty(model.Adjustments);
    }

    // ── BCRA excluded ─────────────────────────────────────────────────────────

    [Fact]
    public void BcraExcluded_UsesTheEffectiveWeights()
    {
        Model model = Build(
            Breakdown() with
            {
                BcraExcluded          = true,
                EffectiveWeightRatios = 0.7692m,
                EffectiveWeightBcra   = 0m,
                EffectiveWeightMacro  = 0.2308m,
            },
            20m);

        Assert.Equal([615.4m, 0m, 184.6m], model.Contributions.Select(part => part.Value));
        Assert.Equal(800m, model.CompositeDisplay);
        Assert.Equal(model.FinalDisplay, LinesTotal(model));
    }

    [Fact]
    public void BcraNotExcluded_UsesTheNominalWeights_WhateverThePersistedOnesSay()
    {
        var (ratios, bcra, macro) = EffectiveWeights(
            Breakdown() with { EffectiveWeightRatios = 0.9m, EffectiveWeightBcra = 0.05m, EffectiveWeightMacro = 0.05m });

        Assert.Equal((0.50m, 0.35m, 0.15m), (ratios, bcra, macro));
    }

    // ── the lines always add up ───────────────────────────────────────────────

    private static readonly Dictionary<string, (Input Input, decimal Score)> AlwaysAddsUpCases = new()
    {
        ["ruta sur"]    = (RutaSur,                                                                                      17.1m),
        ["floor"]       = (Floored,                                                                                      65m),
        ["all three"]   = (Floored with { VentasAdj = 3.33m, CasfogMoraAdj = 1.17m },                                    69.5m),
        ["fractional"]  = (Breakdown() with { ScoreRatios = 31m, ScoreBcra = 47m, ScoreMacro = 19m, VentasAdj = 2.2m },  36.2m),
        ["improvement"] = (Breakdown() with { VentasAdj = -1.4m },                                                       11.6m),
    };

    [Theory]
    [InlineData("ruta sur")]
    [InlineData("floor")]
    [InlineData("all three")]
    [InlineData("fractional")]
    [InlineData("improvement")]
    public void TheLinesAlwaysAddUp(string name)
    {
        var (input, score) = AlwaysAddsUpCases[name];
        Model model = Build(input, score);

        Assert.Equal(model.FinalDisplay, LinesTotal(model));
    }

    // ── formatting ────────────────────────────────────────────────────────────

    [Fact]
    public void FormatFigure_PrintsIntegersAsIsAndFractionsWithTheEsArComma()
    {
        Assert.Equal("845", FormatFigure(845m));
        Assert.Equal("722,5", FormatFigure(722.5m));
        Assert.Equal("1000", FormatFigure(1000m));   // no thousands separator, as String(1000)
    }

    [Fact]
    public void FormatDelta_AlwaysCarriesTheSign()
    {
        Assert.Equal("−15", FormatDelta(-15m));
        Assert.Equal("+50", FormatDelta(50m));
        Assert.Equal("0", FormatDelta(0m));
        Assert.Equal("−2,5", FormatDelta(-2.5m));
    }

    // ── rounding parity with JavaScript's Math.round ──────────────────────────

    [Theory]
    [InlineData(2.5, 3)]
    [InlineData(-2.5, -2)]    // AwayFromZero would give −3
    [InlineData(0.5, 1)]      // banker's (C#'s default) would give 0
    [InlineData(-22.5, -22)]
    [InlineData(0.4999, 0)]
    [InlineData(-0.5, 0)]
    public void JsRound_RoundsHalfTowardPositiveInfinity(double value, int expected)
    {
        Assert.Equal((decimal)expected, JsRound((decimal)value));
    }

    [Fact]
    public void ANegativeMidpointResidual_RoundsLikeThePortal()
    {
        // round1(−2.25) is −2.2 in JavaScript; AwayFromZero would print −2,3.
        Assert.Equal(22m, ToNosisDelta(-2.25m));   // −JsRound(−22.5) = −(−22)
        Assert.Equal(-23m, ToNosisDelta(2.25m));   // −JsRound(22.5)  = −23
    }

    [Fact]
    public void AComponentBetween0And1_UsesTheCanonicalToNosis_NotThePortalFractionRule()
    {
        // Deliberate divergence, recorded: Portal toNosis(0.8) reads 0.8 as a fraction → 200.
        Model model = Build(Breakdown() with { ScoreBcra = 0.8m }, 13m);

        Assert.Equal(990, model.Contributions[1].Nosis);
    }
}
