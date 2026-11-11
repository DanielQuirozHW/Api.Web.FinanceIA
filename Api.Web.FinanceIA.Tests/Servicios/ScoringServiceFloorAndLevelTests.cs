// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-01

using System.Globalization;
using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.ScoringTestHarness;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Pins the critical-ratio floor (Feature 5a) and the risk-level thresholds.
/// <para>
/// GAP THIS FILLS: <see cref="ScoringServiceTests"/> shows a single floor binding once
/// (endeudamiento, 65). It never shows two floors competing, never shows the floor DECLINING to act
/// because the score already exceeds it, never pins the <c>&gt;=</c> at the critical trigger, and
/// only ever reaches the "Low" and "High" levels — so the level ternary's Medium and Critical arms,
/// and all three of its boundary equalities, were unpinned.
/// </para>
/// </summary>
public sealed class ScoringServiceFloorAndLevelTests
{
    // ── 1. The highest applicable floor wins ────────────────────────────────────────────────────
    //
    //   liquidez 1.50 → 0 ; prueba 1.00 → 0 ; leverage 1.00 → 0 ; roe 15 → 0 ; roa 8 → 0
    //   endeudamiento       0.90 >= danger 0.85 → 100  (critical, floor 65)   (w .20) → 20.00
    //   cobertura_intereses 0.80 <= danger 1.00 → 100  (critical, floor 70)   (w .15) → 15.00
    //   margen_neto         4.0  == warn        →  50  (not critical)         (w .07) →  3.50
    //                                                          scoreRatios = 38.50
    //   scoreBeforeFloor = 38.50*0.50 + 0*0.35 + 30*0.15 = 19.25 + 4.50 = 23.75
    //   effectiveFloor   = max(65, 70) = 70.00 → binds → Score 70.00 → "High"

    [Fact]
    public async Task Calculate_WhenTwoRatiosAreCritical_HighestFloorWins()
    {
        var sut = await BuildAsync();

        var ratios = BaselineRatios();
        ratios.DebtRatio        = 0.90;
        ratios.InterestCoverage = 0.80;

        var r = await Score(sut, ratios, BcraNoDebt(), MacroBenign());

        Assert.Equal(38.50m, r.ScoreRatios);
        // Order-independent: CriticalRatios follows RatioFloorKeyMap's enumeration order, which is a
        // Dictionary implementation detail rather than a guarantee.
        Assert.Equal(2, r.CriticalRatios.Count);
        Assert.Contains("endeudamiento", r.CriticalRatios);
        Assert.Contains("cobertura_intereses", r.CriticalRatios);
        Assert.Equal(23.75m, r.ScoreBeforeFloor);
        Assert.Equal(70.00m, r.AppliedFloor);   // cobertura's 70, not endeudamiento's 65
        Assert.Equal(70.00m, r.Score);
        Assert.Equal("High", r.Level);
    }

    // ── 2. The floor only ever RAISES the score, never lowers it ───────────────────────────────
    //
    // All eight ratios beyond danger → every partial 100 and every floor triggered; situación 6 →
    // scoreBcra 100; inflation 15.0 > 10.0 → scoreMacro 35.
    //   scoreBeforeFloor = 100*0.50 + 100*0.35 + 35*0.15 = 50.00 + 35.00 + 5.25 = 90.25
    //   effectiveFloor   = 70.00, but 90.25 is ALREADY above it, so the `scoreFinal < effectiveFloor`
    //   guard is false and the score is untouched. A `scoreFinal = effectiveFloor` refactor would
    //   drag a 90.25 "Critical" borrower down to 70.00 "High" — this is the test that catches it.

    [Fact]
    public async Task Calculate_WhenScoreAlreadyExceedsTheFloor_FloorDoesNotLowerIt()
    {
        var sut = await BuildAsync();

        var r = await Score(sut, BeyondDangerRatios(), BcraDebtFound(6), Macro(15.0));

        Assert.Equal(100.00m, r.ScoreRatios);
        Assert.Equal(100.00m, r.ScoreBcra);
        Assert.Equal(35.00m,  r.ScoreMacro);
        Assert.Equal(8, r.CriticalRatios.Count);

        Assert.Equal(90.25m, r.ScoreBeforeFloor);
        Assert.Equal(90.25m, r.Score);              // unchanged — the floor did not bite
        Assert.Equal("Critical", r.Level);

        // AppliedFloor reports the floor COMPUTED from the eight critical ratios, not one that was
        // applied — it is deliberately non-zero here even though nothing moved. That is why the
        // engine's own guard, and every consumer that renders a floor adjustment, tests
        // `ScoreBeforeFloor < AppliedFloor` rather than `AppliedFloor > 0` (finding D1). Both halves
        // are asserted so the distinction cannot quietly collapse in either direction.
        Assert.Equal(70.00m, r.AppliedFloor);
        Assert.False(r.ScoreBeforeFloor < r.AppliedFloor, "the floor must read as NOT applied here");
    }

    // The companion to the case above: when the floor DOES bind, the same derivation must read true.
    // Together they are what the narrative, chat and PDF renderers depend on.
    [Fact]
    public async Task Calculate_WhenFloorBinds_TheDerivationReadsApplied()
    {
        var sut = await BuildAsync();

        var ratios = BaselineRatios();
        ratios.DebtRatio = 0.90;   // → 100, critical, floor 65

        var r = await Score(sut, ratios, BcraNoDebt(), MacroBenign());

        Assert.Contains("endeudamiento", r.CriticalRatios);
        // endeud 100*.20 + cob 50*.15 + margen 50*.07 = 31.00 ; 31.00*0.50 + 30*0.15 = 20.00
        Assert.Equal(31.00m, r.ScoreRatios);
        Assert.Equal(20.00m, r.ScoreBeforeFloor);
        Assert.Equal(65.00m, r.AppliedFloor);
        Assert.True(r.ScoreBeforeFloor < r.AppliedFloor, "the floor must read as applied here");
        Assert.Equal(65.00m, r.Score);
        Assert.True(r.Score > r.ScoreBeforeFloor, "the floor raised the score");
    }

    // ── 3. The critical trigger is >=, so a partial of EXACTLY 100.00 is critical ───────────────
    //
    // v9: the default trigger is 100 (was 85) and the leverage thresholds are 1.00 / 2.33 / 5.67 (were
    // 1.00 / 1.50 / 2.50), so the boundary moved to leverage's DANGER. The old boundary is kept pinned
    // under an explicit tenant config by the sibling test below.
    //
    // Leverage is the only non-healthy ratio, so scoreRatios is leverage's partial * 0.10.
    //   5.67  → exactly danger → 100.00 → critical → floor 65 binds
    //           scoreRatios 10.00 ; scoreBeforeFloor = 10.00*0.50 + 4.50 = 9.50 → Score 65.00 "High"
    //   5.336 → 50 + (5.336-2.33)/(5.67-2.33)*50 = 95.00 → NOT critical
    //           scoreRatios 9.50 ; Score = 9.50*0.50 + 4.50 = 9.25 "Low"
    // Flipping >= to > turns the first row's Score from 65.00 back into 9.50.

    [Theory]
    [InlineData(5.67,  100.0, true,  65.00, "High")]
    [InlineData(5.336,  95.0, false,  9.25, "Low")]
    public async Task Calculate_CriticalTriggerBoundary_IsInclusive(
        double leverage, double expectedPartial, bool expectedCritical,
        double expectedScore, string expectedLevel)
    {
        var sut = await BuildAsync();

        var ratios = HealthyRatios();
        ratios.Leverage = leverage;

        var r = await Score(sut, ratios, BcraNoDebt(), MacroBenign());

        Assert.Equal((decimal)expectedPartial, Entry(r, "leverage").Score);
        Assert.Equal(expectedCritical, r.CriticalRatios.Contains("leverage"));
        Assert.Equal(expectedCritical ? 65.00m : 0m, r.AppliedFloor);
        Assert.Equal((decimal)expectedScore, r.Score);
        Assert.Equal(expectedLevel, r.Level);
    }

    // ── 3b. The pre-v9 boundary, reproduced by a tenant that still configures it ────────────────
    //
    // This was test 3 until algorithm version 9. A tenant whose ScoringConfig rows still carry the
    // old trigger (85) and leverage thresholds (1.50 / 2.50) must keep exactly the old behaviour: the
    // defaults moved, the arithmetic did not, and config rows win over defaults.
    //   2.20 → 50 + (2.20-1.50)/(2.50-1.50)*50 = 85.00 → critical → floor 65 binds → Score 65.00 "High"
    //   2.18 → 84.00 → NOT critical ; Score = 8.40*0.50 + 4.50 = 8.70 "Low"
    // (2.19 was rejected as the neighbour: it gives 8.725, an exact banker's-rounding midpoint.)

    [Theory]
    [InlineData(2.20, 85.0, true,  65.00, "High")]
    [InlineData(2.18, 84.0, false,  8.70, "Low")]
    public async Task Calculate_CriticalTriggerBoundary_UnderTheOldConfig_IsUnchanged(
        double leverage, double expectedPartial, bool expectedCritical,
        double expectedScore, string expectedLevel)
    {
        var sut = await BuildAsync(
            ("critical_floor_trigger_threshold", 85m),
            ("threshold_leverage_warn",          1.50m),
            ("threshold_leverage_danger",        2.50m));

        var ratios = HealthyRatios();
        ratios.Leverage = leverage;

        var r = await Score(sut, ratios, BcraNoDebt(), MacroBenign());

        Assert.Equal((decimal)expectedPartial, Entry(r, "leverage").Score);
        Assert.Equal(expectedCritical ? "Critico" : "Atencion", Entry(r, "leverage").Status);
        Assert.Equal(expectedCritical, r.CriticalRatios.Contains("leverage"));
        Assert.Equal(expectedCritical ? 65.00m : 0m, r.AppliedFloor);
        Assert.Equal(85m, r.CriticalFloorTrigger);
        Assert.Equal((decimal)expectedScore, r.Score);
        Assert.Equal(expectedLevel, r.Level);
    }

    // ── 4. Level thresholds, pinned AT each boundary ────────────────────────────────────────────
    //
    // The level ternary is `<= thLow ? Low : <= thMedium ? Medium : <= thHigh ? High : Critical`
    // with 35 / 60 / 80. Hitting those values exactly through the weighted arithmetic is awkward, so
    // the floor is used as a dial instead: when it binds, Score == Round(effectiveFloor, 2) exactly.
    //
    // Fixture: endeudamiento 0.90 → 100 (critical), every other ratio at or better than its ok
    // anchor → 0. scoreRatios = 100 * 0.20 = 20.00 ; scoreBeforeFloor = 20.00*0.50 + 30*0.15 = 14.50.
    // 14.50 is below every floor below, so the floor binds in all six rows and Score IS the floor.
    //
    // Each pair straddles a boundary by one hundredth: the lower value must stay in the lower band
    // (proving <= is inclusive), the upper must fall into the next one.

    [Theory]
    [InlineData("35.00", "Low")]
    [InlineData("35.01", "Medium")]
    [InlineData("60.00", "Medium")]
    [InlineData("60.01", "High")]
    [InlineData("80.00", "High")]
    [InlineData("80.01", "Critical")]
    public async Task Calculate_LevelThresholds_AreInclusiveAtEachBoundary(
        string floor, string expectedLevel)
    {
        var floorValue = decimal.Parse(floor, CultureInfo.InvariantCulture);

        var sut = await BuildAsync(("critical_floor_endeudamiento", floorValue));

        var ratios = BaselineRatios();
        ratios.DebtRatio        = 0.90;   // → 100, critical
        ratios.InterestCoverage = 3.00;   // == ok → 0
        ratios.NetMargin        = 10.0;   // == ok → 0

        var r = await Score(sut, ratios, BcraNoDebt(), MacroBenign());

        Assert.Equal(20.00m, r.ScoreRatios);
        Assert.Equal(14.50m, r.ScoreBeforeFloor);   // safely below every floor under test
        Assert.Equal(floorValue, r.AppliedFloor);
        Assert.Equal(floorValue, r.Score);
        Assert.Equal(expectedLevel, r.Level);
    }

    // ── 5. A configured floor ABOVE 100 cannot carry the score off the 0-100 scale ──────────────
    //
    // The composite is clamped to [0,100], but the floor used to be applied AFTER that clamp and
    // un-clamped, so a floor of 150 simply became the score — 150 on a 0-100 scale. The floor is
    // now clamped at the point it is used, which bounds the engine by construction.
    //
    // Same dial fixture as case 4, seeded at 150 instead:
    //   endeudamiento 0.90 → 100 (>= the 100 trigger → critical) ; every other partial 0
    //     scoreRatios      = 100 * 0.20 = 20.00
    //     scoreBeforeFloor = 20.00*0.50 + 0*0.35 + 30*0.15 = 10.00 + 4.50 = 14.50
    //     effectiveFloor   = Clamp(150, 0, 100) = 100.00 ; 14.50 < 100.00 → Score 100.00
    //     100.00 > 80 → "Critical"
    //   Un-clamped this read Score 150.00. AppliedFloor is asserted at the CLAMPED 100.00, not the
    //   configured 150: every consumer recovers "did the floor bind?" as
    //   ScoreBeforeFloor < AppliedFloor, so AppliedFloor must be the floor that really applied.
    //
    // SettingsService now rejects a floor of 150 outright (see SettingsServiceScoringValidationTests);
    // this covers the second layer — a row already in the DB that predates that check.

    [Fact]
    public async Task Calculate_WhenTheConfiguredFloorExceeds100_TheAppliedFloorIsClampedTo100()
    {
        var sut = await BuildAsync(("critical_floor_endeudamiento", 150.00m));

        var ratios = BaselineRatios();
        ratios.DebtRatio        = 0.90;   // → 100, critical
        ratios.InterestCoverage = 3.00;   // == ok → 0
        ratios.NetMargin        = 10.0;   // == ok → 0

        var r = await Score(sut, ratios, BcraNoDebt(), MacroBenign());

        Assert.Contains("endeudamiento", r.CriticalRatios);
        Assert.Equal(20.00m, r.ScoreRatios);
        Assert.Equal(14.50m, r.ScoreBeforeFloor);

        Assert.Equal(100.00m, r.AppliedFloor);   // the clamped floor, not the configured 150
        Assert.Equal(100.00m, r.Score);          // 150.00 would be off a 0-100 scale
        Assert.Equal("Critical", r.Level);
    }
}
