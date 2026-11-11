// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-01

using Api.Web.FinanceIA.Models.Financia;
using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.ScoringTestHarness;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Pins <c>ScoringService.PartialScore</c> — the linear interpolation every ratio goes through — and
/// the <c>RatioStatus</c> labels derived from it.
/// <para>
/// GAP THIS FILLS: every value <see cref="ScoringServiceTests"/> feeds sits either EXACTLY on a
/// threshold anchor (ok → 0, warn → 50, danger → 100) or strictly beyond the OK anchor, so the two
/// early returns to 0 already execute there. What had NEVER executed is the whole middle of the
/// function: none of the four interpolation expressions, in either direction, and no value strictly
/// beyond the DANGER anchor (the existing fixtures only ever land on danger exactly). A sign flip or
/// a swapped denominator in any of the four would not have been caught.
/// </para>
/// <para>
/// Run with an EMPTY ScoringConfigs table, so every threshold comes from <see cref="ScoringDefaults"/>.
/// PartialScore infers direction from the defaults themselves: higher-is-better when ok &gt; danger
/// (liquidez, prueba, cobertura, roe, roa, margen), lower-is-better otherwise (endeudamiento, leverage).
/// Remember these are RISK scores — 0 is healthy, 100 is maximum risk.
/// </para>
/// <para>
/// Every exact expectation here relies on PartialScore's <c>(decimal)value.Value</c> cast turning the
/// double literal into precisely the decimal it is written as. That holds because the conversion
/// rounds to 15 significant digits, and every literal used below is written with far fewer.
/// </para>
/// </summary>
public sealed class ScoringServicePartialScoreTests
{
    // ── 1. Linear interpolation strictly BETWEEN anchors, both directions, all four expressions ──
    //
    // One fixture puts every ratio in an interior band, so all four interpolation expressions run
    // and the weighted composite is pinned on top of them. Values are deliberately NOT band
    // midpoints — a wrong denominator would still produce 25/75 from a midpoint and go unnoticed.
    //
    //   liquidez_corriente  1.40  ok 1.50 / warn 1.00      → (1.50-1.40)/(1.50-1.00)*50 = 10.00  (w .20) → 2.00
    //   prueba_acida        0.64  warn 0.70 / danger 0.50  → 50+(0.70-0.64)/(0.70-0.50)*50 = 65.00 (w .10) → 6.50
    //   endeudamiento       0.55  ok 0.50 / warn 0.70      → (0.55-0.50)/(0.70-0.50)*50 = 12.50  (w .20) → 2.50
    //   leverage            4.334 warn 2.33 / danger 5.67  → 50+(4.334-2.33)/(5.67-2.33)*50 = 80.00 (w .10) → 8.00
    //     (v9: was 2.10 under warn 1.50 / danger 2.50; moved so the partial stays exactly 80 under the
    //      endeudamiento-equivalent leverage thresholds and every figure below is unchanged)
    //   cobertura_intereses 2.70  ok 3.00 / warn 1.50      → (3.00-2.70)/(3.00-1.50)*50 = 10.00  (w .15) → 1.50
    //   roe                 4.0   warn 5 / danger 0        → 50+(5-4)/(5-0)*50 = 60.00           (w .10) → 6.00
    //   roa                 7.0   ok 8 / warn 3            → (8-7)/(8-3)*50 = 10.00              (w .08) → 0.80
    //   margen_neto         3.2   warn 4 / danger 0        → 50+(4-3.2)/(4-0)*50 = 60.00         (w .07) → 4.20
    //                                                                      scoreRatios = 31.50
    //   No partial reaches the 100 trigger, so no floor clamps the composite.
    //   Score = 31.50*0.50 + 0*0.35 + 30*0.15 = 15.75 + 4.50 = 20.25 → "Low"

    [Fact]
    public async Task Calculate_PartialScore_InterpolatesLinearly_InBothDirections()
    {
        var sut = await BuildAsync();

        var ratios = new RatiosData
        {
            CurrentRatio     = 1.40,
            AcidTest         = 0.64,
            DebtRatio        = 0.55,
            Leverage         = 4.334,
            InterestCoverage = 2.70,
            Roe              = 4.0,
            Roa              = 7.0,
            NetMargin        = 3.2,
        };

        var r = await Score(sut, ratios, BcraNoDebt(), MacroBenign());

        Assert.Equal(10.00m, Entry(r, "liquidez_corriente").Score);
        Assert.Equal(65.00m, Entry(r, "prueba_acida").Score);
        Assert.Equal(12.50m, Entry(r, "endeudamiento").Score);
        Assert.Equal(80.00m, Entry(r, "leverage").Score);
        Assert.Equal(10.00m, Entry(r, "cobertura_intereses").Score);
        Assert.Equal(60.00m, Entry(r, "roe").Score);
        Assert.Equal(10.00m, Entry(r, "roa").Score);
        Assert.Equal(60.00m, Entry(r, "margen_neto").Score);

        Assert.Equal(31.50m, r.ScoreRatios);
        Assert.Equal(20.25m, r.Score);
        Assert.Equal("Low",  r.Level);

        // The interpolation must not have tripped the floor machinery.
        Assert.Empty(r.CriticalRatios);
        Assert.Equal(0m, r.AppliedFloor);
        Assert.Equal(r.Score, r.ScoreBeforeFloor);
    }

    // ── 2. Clamp to 0 STRICTLY beyond the ok anchor (both directions) ───────────────────────────

    [Fact]
    public async Task Calculate_PartialScore_ClampsToZero_StrictlyBeyondOkAnchor()
    {
        var sut = await BuildAsync();

        // Every value is well past its ok anchor, in whichever direction that ratio runs.
        var ratios = new RatiosData
        {
            CurrentRatio     = 5.00,   // higher-is-better, >> ok 1.50
            AcidTest         = 3.00,   // >> ok 1.00
            DebtRatio        = 0.05,   // lower-is-better, << ok 0.50
            Leverage         = 0.10,   // << ok 1.00
            InterestCoverage = 99.0,   // >> ok 3.00
            Roe              = 200.0,  // >> ok 15
            Roa              = 50.0,   // >> ok 8
            NetMargin        = 80.0,   // >> ok 10
        };

        var r = await Score(sut, ratios, BcraNoDebt(), MacroBenign());

        Assert.All(r.Breakdown.Values, e => Assert.Equal(0m, e.Score));
        Assert.All(r.Breakdown.Values, e => Assert.Equal("Saludable", e.Status));

        Assert.Equal(0.00m, r.ScoreRatios);
        Assert.Empty(r.CriticalRatios);

        // Score = 0*0.50 + 0*0.35 + 30*0.15 = 4.50
        Assert.Equal(4.50m, r.Score);
        Assert.Equal("Low", r.Level);
    }

    // ── 3. Clamp to 100 STRICTLY beyond the danger anchor — and the floor max, BOTH arms ────────
    //
    // All eight partials 100 → every ratio critical → the floor loop walks RatioFloorKeyMap in
    // insertion order with floors 60, 60, 65, 65, 70, 55, 55, 55. That exercises both arms of
    // `if (floor > effectiveFloor)`: it climbs 60 → 65 → 70, and the three 55s that iterate AFTER
    // the 70 must not pull it back down. A `= floor` instead of a guarded max would end at 55.
    //
    //   scoreRatios      = 100 * 1.00 = 100.00
    //   scoreBeforeFloor = 100*0.50 + 0*0.35 + 30*0.15 = 50.00 + 4.50 = 54.50
    //   effectiveFloor   = max(60,60,65,65,70,55,55,55) = 70.00  → binds, 54.50 < 70.00
    //   Score 70.00 → "High" (60 < 70 <= 80); note the floor also MOVED the level up from "Medium".

    [Fact]
    public async Task Calculate_PartialScore_ClampsTo100_StrictlyBeyondDangerAnchor_AndHighestFloorWins()
    {
        var sut = await BuildAsync();

        var r = await Score(sut, BeyondDangerRatios(), BcraNoDebt(), MacroBenign());

        Assert.All(r.Breakdown.Values, e => Assert.Equal(100m, e.Score));
        Assert.All(r.Breakdown.Values, e => Assert.Equal("Critico", e.Status));

        Assert.Equal(100.00m, r.ScoreRatios);
        Assert.Equal(8, r.CriticalRatios.Count);

        Assert.Equal(54.50m, r.ScoreBeforeFloor);
        Assert.Equal(70.00m, r.AppliedFloor);   // cobertura_intereses' 70 — the highest, not the last
        Assert.Equal(70.00m, r.Score);
        Assert.Equal("High", r.Level);
    }

    // ── 4. Every ratio null → the `survivingWeightSum > 0m` conjunct, and the fallback shape ────
    //
    // survivingWeightSum is 0, so the renormalise branch is refused twice over (0 < 0.50 AND not > 0)
    // and the low-coverage fallback runs. The non-obvious part, and what this pins: because
    // renormaliseRatios stays FALSE, NOT ONE entry is marked Excluded and every entry keeps its full
    // NOMINAL weight — even though all eight are listed in NonComputableRatios. Asserting
    // Excluded == true here would be wrong.
    //
    //   scoreRatios = 50 * 1.00 = 50.00 ; Score = 50*0.50 + 0*0.35 + 30*0.15 = 25.00 + 4.50 = 29.50

    [Fact]
    public async Task Calculate_WhenEveryRatioIsNull_FallsBackWithZeroSurvivingWeight()
    {
        var sut = await BuildAsync();

        var r = await Score(sut, new RatiosData(), BcraNoDebt(), MacroBenign());

        Assert.True(r.RatiosLowCoverageFallback);
        Assert.Equal(0m, r.RatiosSurvivingWeight);
        Assert.Equal(8, r.NonComputableRatios.Count);

        Assert.All(r.Breakdown.Values, e => Assert.Equal(50m, e.Score));
        Assert.All(r.Breakdown.Values, e => Assert.Equal("Atencion", e.Status));
        Assert.All(r.Breakdown.Values, e => Assert.False(e.Excluded));
        Assert.Equal(0.2000m, Entry(r, "liquidez_corriente").EffectiveWeight);   // nominal, not 0
        Assert.Equal(0.0700m, Entry(r, "margen_neto").EffectiveWeight);

        Assert.Equal(50.00m, r.ScoreRatios);
        Assert.Empty(r.CriticalRatios);          // 50 is below the 100 trigger
        Assert.Equal(29.50m, r.Score);
        Assert.Equal("Low", r.Level);
    }

    // ── 5. A single-ratio sweep across every band — lower-is-better (leverage) ──────────────────
    //
    // Leverage: ok 1.00 < warn 2.33 < danger 5.67 (v9: the endeudamiento 0.50 / 0.70 / 0.85 anchors
    // restated as L = E/(1-E); the rows were re-derived from 1.00 / 1.50 / 2.50, and the trigger moved
    // from 85 to 100, so "Critico" now starts exactly at danger). Every other ratio is held at
    // HealthyRatios (all partials 0), so leverage is the only moving part. This sweep walks all four
    // PartialScore branches AND both RatioStatus cutoffs, which are both STRICT `<`:
    //   Saludable = [0,50)   Atencion = [50,trigger)   Critico = [trigger,100]
    //
    // Inputs are chosen so every partial is exact in decimal: 5.66 would give 99.85, whose 0.10-weight
    // composite 9.985 rounds to even and breaks the reproduction below.

    [Theory]
    [InlineData(0.50,   0.0,   "Saludable")]  // <= ok            → early return 0
    [InlineData(1.665,  25.0,  "Saludable")]  // (1.665-1.00)/(2.33-1.00)*50
    [InlineData(2.3034, 49.0,  "Saludable")]  // (2.3034-1.00)/(2.33-1.00)*50 — just under the 50 cutoff
    [InlineData(2.33,   50.0,  "Atencion")]   // == warn          → exactly 50, so NOT "Saludable"
    [InlineData(5.6366, 99.5,  "Atencion")]   // 50+(5.6366-2.33)/(5.67-2.33)*50 — just under the 100 trigger
    [InlineData(5.67,   100.0, "Critico")]    // == danger        → exactly 100, so "Critico"
    [InlineData(6.00,   100.0, "Critico")]    // >= danger        → early return 100
    public async Task Calculate_PartialScore_LowerIsBetter_SweepsEveryBand(
        double leverage, double expectedScore, string expectedStatus)
    {
        var sut = await BuildAsync();

        var ratios = HealthyRatios();
        ratios.Leverage = leverage;

        var r = await Score(sut, ratios, BcraNoDebt(), MacroBenign());

        var entry = Entry(r, "leverage");
        Assert.Equal((decimal)expectedScore, entry.Score);
        Assert.Equal(expectedStatus, entry.Status);

        // Every other ratio is at 0, so the composite is leverage alone at its 0.10 weight.
        Assert.Equal(Math.Round((decimal)expectedScore * 0.10m, 2), r.ScoreRatios);
    }

    // ── 6. The same sweep in the other direction — higher-is-better (liquidez corriente) ────────
    //
    // liquidez_corriente: ok 1.50 > warn 1.00 > danger 0.70. Mirror image of the sweep above; this
    // is the direction whose two interpolation expressions had never run at a non-anchor value.

    [Theory]
    [InlineData(2.00, 0.0,   "Saludable")]  // >= ok             → early return 0
    [InlineData(1.25, 25.0,  "Saludable")]  // (1.50-1.25)/(1.50-1.00)*50
    [InlineData(1.01, 49.0,  "Saludable")]  // (1.50-1.01)/(1.50-1.00)*50 — just under the 50 cutoff
    [InlineData(1.00, 50.0,  "Atencion")]   // == warn           → exactly 50
    [InlineData(0.79, 85.0,  "Atencion")]   // 50+(1.00-0.79)/(1.00-0.70)*50 — 85, "Critico" until v9 moved the trigger to 100
    [InlineData(0.71, 98.33, "Atencion")]   // 50+(1.00-0.71)/(1.00-0.70)*50 — just short of danger, still not critical
    [InlineData(0.70, 100.0, "Critico")]    // == danger         → exactly 100, the v9 trigger
    [InlineData(0.50, 100.0, "Critico")]    // <= danger         → early return 100
    public async Task Calculate_PartialScore_HigherIsBetter_SweepsEveryBand(
        double currentRatio, double expectedScore, string expectedStatus)
    {
        var sut = await BuildAsync();

        var ratios = HealthyRatios();
        ratios.CurrentRatio = currentRatio;

        var r = await Score(sut, ratios, BcraNoDebt(), MacroBenign());

        var entry = Entry(r, "liquidez_corriente");
        Assert.Equal((decimal)expectedScore, entry.Score);
        Assert.Equal(expectedStatus, entry.Status);

        Assert.Equal(Math.Round((decimal)expectedScore * 0.20m, 2), r.ScoreRatios);
    }
}
