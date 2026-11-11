// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-01

using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.ScoringTestHarness;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Pins the macro component.
/// <para>
/// GAP THIS FILLS: <see cref="ScoringServiceTests"/> only ever passes <c>MacroBenign</c> (scoreMacro
/// 30) or <c>null</c>, and never asserts <c>ScoreMacro</c> for the null case. The inflation penalty
/// arm had never executed, and the strictness of its <c>&gt;</c> comparison was unpinned.
/// </para>
/// <para>
/// All fixtures use <see cref="ScoringTestHarness.BaselineRatios"/> (scoreRatios 21.00) and
/// <c>NoDebtReported</c> (scoreBcra 0, included), so
/// <c>Score = 21.00*0.50 + 0*0.35 + scoreMacro*0.15 = 10.50 + 0.15 * scoreMacro</c>.
/// </para>
/// </summary>
public sealed class ScoringServiceMacroTests
{
    // scoreMacro starts at a base of 30 whenever a payload is present, and gains the 5-point penalty
    // only when inflation is STRICTLY above the 10.00 threshold.
    [Theory]
    [InlineData(5.0,   30.0, 15.00)]  // below threshold        → base only
    [InlineData(10.0,  30.0, 15.00)]  // EXACTLY at it          → `>` is strict, so still no penalty
    [InlineData(10.01, 35.0, 15.75)]  // a hundredth above it   → +5
    [InlineData(15.0,  35.0, 15.75)]  // well above it          → +5, not more
    public async Task Calculate_MacroInflationPenalty_AppliesOnlyStrictlyAboveThreshold(
        double inflacionMensual, double expectedScoreMacro, double expectedScore)
    {
        var sut = await BuildAsync();

        var r = await Score(sut, BaselineRatios(), BcraNoDebt(), Macro(inflacionMensual));

        Assert.Equal((decimal)expectedScoreMacro, r.ScoreMacro);
        Assert.Equal((decimal)expectedScore, r.Score);
    }

    // No macro payload at all → the neutral 50. Note this is NOT the same treatment BCRA gets: macro
    // is never excluded and never renormalised away, it just scores 50 at its full 0.15 weight.
    //   Score = 10.50 + 50*0.15 = 10.50 + 7.50 = 18.00
    [Fact]
    public async Task Calculate_WhenMacroDataIsAbsent_ScoresNeutral50_AndIsNotExcluded()
    {
        var sut = await BuildAsync();

        var r = await Score(sut, BaselineRatios(), BcraNoDebt(), macro: null);

        Assert.Equal(50.00m, r.ScoreMacro);
        Assert.Equal(0.1500m, r.EffectiveWeightMacro);   // full nominal weight — no exclusion
        Assert.Equal(18.00m, r.Score);
        Assert.Equal("Low", r.Level);
    }

    // The `if (scoreMacro > 100m) scoreMacro = 100m;` clamp is unreachable on the defaults (the most
    // macro can score is 30 + 5 = 35), so it needs a seeded penalty to execute at all.
    //   30 + 100 = 130 → clamped to 100 ; Score = 10.50 + 100*0.15 = 10.50 + 15.00 = 25.50
    [Fact]
    public async Task Calculate_WhenSeededPenaltyOverflows_MacroClampsTo100()
    {
        var sut = await BuildAsync(("macro_penalizacion_puntos", 100m));

        var r = await Score(sut, BaselineRatios(), BcraNoDebt(), Macro(12.0));

        Assert.Equal(100.00m, r.ScoreMacro);
        Assert.Equal(25.50m, r.Score);
        Assert.Equal("Low", r.Level);
    }

    // Finding D4, now FIXED — this asserts the fix, it no longer documents the defect.
    // BcraService.GetMacroAsync returns null only when BOTH of its variables fail, so a payload with
    // InflacionMensual == null and TasaReferencia set is a real, reachable shape. It used to score
    // 30 — the BEST macro outcome available — where a total outage scores 50 and a confirmed
    // above-threshold reading scores 35, so losing the only macro variable the engine reads made a
    // borrower look healthier. The figure the base and the penalty are ABOUT is gone here, so the
    // payload now falls to the same neutral 50 an absent payload produces.
    //   Score = 10.50 + 50*0.15 = 10.50 + 7.50 = 18.00
    [Fact]
    public async Task Calculate_WhenMacroPayloadHasNoInflationFigure_ScoresTheNeutral50()
    {
        var sut = await BuildAsync();

        var r = await Score(sut, BaselineRatios(), BcraNoDebt(), Macro(inflacionMensual: null, tasaReferencia: 21.56));

        Assert.Equal(50.00m, r.ScoreMacro);   // identical to an absent payload, NOT the benign base
        Assert.Equal(18.00m, r.Score);        // was 15.00 while the missing figure earned the 30 base
    }

    // The monotonicity invariant, pinned as a SET. Read as risk (0 healthy, 100 maximum risk), the
    // four reachable macro shapes must order like this — losing the inflation figure can never score
    // BETTER than knowing it:
    //   no macro payload at all           → 50   (neutral — nothing known)
    //   payload, inflation UNKNOWN        → 50   (same neutral — the figure scoring reads is gone)
    //   payload, inflation at/below 10.00 → 30   (base — the good, CONFIRMED outcome)
    //   payload, inflation above 10.00    → 35   (base + the 5-point penalty)
    // Asserted together on purpose: any one of these can be moved on its own without the ordering
    // noticing, which is exactly how D4 got in. The four Score figures follow the file's baseline
    // identity, Score = 10.50 + 0.15 * scoreMacro.
    [Fact]
    public async Task Calculate_MacroScore_IsMonotoneInInflationDataAvailability()
    {
        var sut = await BuildAsync();

        var noMacro          = await Score(sut, BaselineRatios(), BcraNoDebt(), macro: null);
        var inflationUnknown = await Score(sut, BaselineRatios(), BcraNoDebt(), Macro(null, tasaReferencia: 21.56));
        var belowThreshold   = await Score(sut, BaselineRatios(), BcraNoDebt(), Macro(5.0));
        var aboveThreshold   = await Score(sut, BaselineRatios(), BcraNoDebt(), Macro(15.0));

        Assert.Equal(50.00m, noMacro.ScoreMacro);
        Assert.Equal(50.00m, inflationUnknown.ScoreMacro);
        Assert.Equal(30.00m, belowThreshold.ScoreMacro);
        Assert.Equal(35.00m, aboveThreshold.ScoreMacro);

        Assert.Equal(18.00m, noMacro.Score);            // 10.50 + 50*0.15 = 10.50 + 7.50
        Assert.Equal(18.00m, inflationUnknown.Score);   // 10.50 + 50*0.15 = 10.50 + 7.50
        Assert.Equal(15.00m, belowThreshold.Score);     // 10.50 + 30*0.15 = 10.50 + 4.50
        Assert.Equal(15.75m, aboveThreshold.Score);     // 10.50 + 35*0.15 = 10.50 + 5.25

        // The invariant itself, stated relationally so it survives a change to any single figure
        // above: an unknown inflation figure reads exactly like a total outage, and never scores
        // lower (healthier) than a confirmed reading of either kind.
        Assert.Equal(noMacro.ScoreMacro, inflationUnknown.ScoreMacro);
        Assert.True(inflationUnknown.ScoreMacro > belowThreshold.ScoreMacro);
        Assert.True(inflationUnknown.ScoreMacro > aboveThreshold.ScoreMacro);
    }
}
