// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-01

using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.ScoringTestHarness;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Pins <c>ScoringService.LoadConfigAsync</c> and the paths only reachable with a tenant override.
/// <para>
/// GAP THIS FILLS: no test in the whole project had ever inserted a <c>ScoringConfig</c> row. Every
/// existing assertion runs on <see cref="ScoringDefaults"/> with sector "General", so the tenant
/// predicate, the base dictionary, the <c>__sector__</c> overlay, every <c>Get</c> hit-arm, the
/// degenerate component-weight guard and the <c>CalculateAsync</c> catch-all were all unexercised.
/// </para>
/// <para>
/// Fixtures use <see cref="ScoringTestHarness.BaselineRatios"/> (scoreRatios 21.00) unless a case
/// needs otherwise, with <c>NoDebtReported</c> (scoreBcra 0) and <c>MacroBenign</c> (scoreMacro 30),
/// so the unmodified composite is <c>21.00*0.50 + 0*0.35 + 30*0.15 = 15.00</c>.
/// </para>
/// </summary>
public sealed class ScoringServiceConfigTests
{
    // ── 1. A seeded row actually overrides the default ─────────────────────────────────────────
    //
    // macro_penalizacion_puntos defaults to 5; seeded at 20, inflation 12.0 (> the 10.00 threshold)
    // gives scoreMacro = 30 + 20 = 50 instead of 35.
    //   Score = 10.50 + 50*0.15 = 10.50 + 7.50 = 18.00   (default config would give 15.75)

    [Fact]
    public async Task Calculate_SeededConfigRow_OverridesTheDefault()
    {
        var sut = await BuildAsync(("macro_penalizacion_puntos", 20m));

        var r = await Score(sut, BaselineRatios(), BcraNoDebt(), Macro(12.0));

        Assert.Equal(50.00m, r.ScoreMacro);
        Assert.Equal(18.00m, r.Score);
    }

    // ── 2. A row belonging to another tenant must not leak ─────────────────────────────────────
    //
    // The identical override as case 1, but stored against a different TenantId. LoadConfigAsync
    // filters on TenantId, so the scored tenant must still see the default penalty of 5.

    [Fact]
    public async Task Calculate_ConfigRowForAnotherTenant_DoesNotLeak()
    {
        var sut = await BuildFromRowsAsync(Row(OtherTenant, "macro_penalizacion_puntos", 20m));

        var r = await Score(sut, BaselineRatios(), BcraNoDebt(), Macro(12.0));

        Assert.Equal(35.00m, r.ScoreMacro);   // 30 + the DEFAULT 5, not the other tenant's 20
        Assert.Equal(15.75m, r.Score);
    }

    // ── 3. The __sector__ overlay applies to its own sector and nothing else ───────────────────
    //
    // One seeded row: threshold_liquidez_corriente_ok__sector__Industria = 3.00 (default 1.50).
    // Rows containing "__sector__" are kept out of the base dictionary, then re-applied — with the
    // suffix stripped — only when the canonical sector matches.
    //
    //   With the overlay (ok 3.00, warn 1.00): liquidez 1.50 → (3.00-1.50)/(3.00-1.00)*50 = 37.50
    //     scoreRatios = 21.00 + 37.50*0.20 = 21.00 + 7.50 = 28.50   (it contributed 0 before)
    //     Score       = 28.50*0.50 + 4.50 = 14.25 + 4.50 = 18.75
    //   Without it (ok 1.50): liquidez 1.50 >= ok → 0 → scoreRatios 21.00 → Score 15.00

    [Theory]
    [InlineData("Industria", 28.50, 18.75, "Industria")]  // exact match      → overlay applies
    [InlineData("industria", 28.50, 18.75, "Industria")]  // canonicalised    → overlay applies
    [InlineData("Agro",      21.00, 15.00, "Agro")]       // different sector → row does not match
    [InlineData(null,        21.00, 15.00, "General")]    // General          → overlay skipped
    public async Task Calculate_SectorOverlay_AppliesOnlyToItsOwnSector(
        string? entitySector, double expectedScoreRatios, double expectedScore, string expectedProfile)
    {
        var sut = await BuildAsync(
            ("threshold_liquidez_corriente_ok__sector__Industria", 3.00m));

        var r = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), entitySector);

        Assert.Equal((decimal)expectedScoreRatios, r.ScoreRatios);
        Assert.Equal((decimal)expectedScore, r.Score);
        Assert.Equal(expectedProfile, r.SectorProfile);
    }

    // ── 3b. A sector row BEATS a tenant-wide base row for the same key ──────────────────────────
    //
    // Case 3 seeds only the sector row, so the overlay's `baseCfg[baseKey] = ...` ADDS a key. Here a
    // base row for the same key exists, so the indexer must OVERWRITE it — that is the precedence
    // rule the overlay exists for. A refactor to TryAdd (base wins) would keep case 3 green while
    // silently inverting config precedence in production; only this test catches it.
    //
    //   base only  (ok 2.00): liquidez 1.50 → (2.00-1.50)/(2.00-1.00)*50 = 25.00
    //                         scoreRatios = 21.00 + 25.00*0.20 = 26.00 → Score 13.00 + 4.50 = 17.50
    //   + overlay  (ok 3.00): liquidez 1.50 → (3.00-1.50)/(3.00-1.00)*50 = 37.50
    //                         scoreRatios = 21.00 + 37.50*0.20 = 28.50 → Score 14.25 + 4.50 = 18.75

    [Theory]
    [InlineData("Industria", 37.50, 28.50, 18.75)]   // sector row wins over the base row
    [InlineData(null,        25.00, 26.00, 17.50)]   // General → base row stands
    public async Task Calculate_SectorOverlay_OverwritesTheTenantWideBaseRow(
        string? entitySector, double expectedPartial, double expectedScoreRatios, double expectedScore)
    {
        var sut = await BuildAsync(
            ("threshold_liquidez_corriente_ok",                    2.00m),
            ("threshold_liquidez_corriente_ok__sector__Industria", 3.00m));

        var r = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), entitySector);

        Assert.Equal((decimal)expectedPartial, Entry(r, "liquidez_corriente").Score);
        Assert.Equal((decimal)expectedScoreRatios, r.ScoreRatios);
        Assert.Equal((decimal)expectedScore, r.Score);
    }

    // ── 4. Sector canonicalisation, echoed onto the result ─────────────────────────────────────

    [Theory]
    [InlineData(null,                "General")]
    [InlineData("",                  "General")]
    [InlineData("   ",               "General")]
    [InlineData("Nonsense",          "General")]
    [InlineData("INDUSTRIA",         "Industria")]
    [InlineData(" Agro ",            "Agro")]
    [InlineData("Construccion",      "Construccion")]
    [InlineData("Construcción", "Construccion")]   // accented → diacritics stripped
    public async Task Calculate_SectorProfile_IsCanonicalised(string? raw, string expected)
    {
        var sut = await BuildAsync();

        var r = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), raw);

        Assert.Equal(expected, r.SectorProfile);
        Assert.Equal(15.00m, r.Score);   // no sector rows seeded → thresholds identical either way
    }

    // ── 4b. The bcra_trend_* keys are configurable too ──────────────────────────────────────────
    //
    // ScoringServiceBcraTests exercises the whole trend block, but always on the defaults, so those
    // five Get() calls were only ever observed on their fallback arm.
    //
    //   situación 5 (base 90) vs comparison 1 → delta +4
    //   seeded: adj = min(4 * 3, 5) = 5   (defaults would give min(4 * 8, 30) = 30)
    //   scoreBcra = 90 + 5 = 95 ; Score = 15.00 + 0.35*95 = 15.00 + 33.25 = 48.25 → "Medium"
    //   On the defaults this same fixture gives adj 30, scoreBcra 100 and Score 50.00, so the seed
    //   demonstrably changes the outcome.
    //
    // The two periods span exactly the six-month default window ("190012" anchors the cutoff at
    // "190006"), so the comparison is selected normally rather than through the historial[^1]
    // fallback. Both entries carry situación 1, so the delta would be 4 either way — the spacing is
    // about pinning the intended branch, not the arithmetic.

    [Fact]
    public async Task Calculate_BcraTrend_HonoursSeededPerStepPointsAndCap()
    {
        var sut = await BuildAsync(
            ("bcra_trend_pts_per_step_worsen", 3m),
            ("bcra_trend_max_worsen_adj",      5m));

        var bcra = BcraDebtFound(5, Historial(("190012", 1), ("190006", 1)));

        var r = await Score(sut, BaselineRatios(), bcra, MacroBenign());

        Assert.Equal(4, r.BcraTrendDelta);
        Assert.Equal(5.00m, r.BcraTrendAdj);    // the seeded cap, not the default 30
        Assert.Equal(95.00m, r.ScoreBcra);
        Assert.Equal(48.25m, r.Score);
        Assert.Equal("Medium", r.Level);
    }

    // ── 5. An EXCLUDED ratio is skipped by the critical floor — made falsifiable ────────────────
    //
    // The guard `if (excludedRatioKeys.Contains(ratioKey)) continue;` is unreachable under the
    // default config: a non-computable ratio always scores the neutral 50, which never clears the
    // trigger (100 since v9, 85 before), so the source comment calls it "harmless in practice". That is only true while the
    // trigger stays above 50. Seeded at 45, the placeholder DOES clear it and the guard becomes
    // load-bearing — this is what turns a dead branch into a real test.
    //
    //   cobertura n/d → excluded; 0.85 survives → renormalise
    //     survivorWeighted = endeud 50*.20 + margen 50*.07 = 10.00 + 3.50 = 13.50
    //     scoreRatios      = 13.50 / 0.85 = 15.882…  → reported 15.88
    //     scoreBeforeFloor = 13.50/0.85*0.50 + 0*0.35 + 30*0.15 = 7.941… + 4.50 = 12.441… → 12.44
    //   Floor loop at trigger 45 — partials at or above it are endeudamiento 50, margen_neto 50 and
    //   cobertura_intereses 50, but cobertura is excluded and skipped:
    //     CriticalRatios = [endeudamiento, margen_neto] ; effectiveFloor = max(65, 55) = 65.00
    //     12.44 < 65.00 → Score 65.00 → "High"
    //   WITHOUT the guard cobertura's floor of 70 would win and the score would be 70.00. That 5-point
    //   difference is the whole assertion.

    [Fact]
    public async Task Calculate_ExcludedRatio_IsSkippedByTheCriticalFloor()
    {
        var sut = await BuildAsync(("critical_floor_trigger_threshold", 45.00m));

        var ratios = BaselineRatios();
        ratios.InterestCoverage = null;

        var r = await Score(sut, ratios, BcraNoDebt(), MacroBenign());

        Assert.Equal(15.88m, r.ScoreRatios);
        Assert.Equal(12.44m, r.ScoreBeforeFloor);

        Assert.Equal(2, r.CriticalRatios.Count);
        Assert.Contains("endeudamiento", r.CriticalRatios);
        Assert.Contains("margen_neto", r.CriticalRatios);
        Assert.DoesNotContain("cobertura_intereses", r.CriticalRatios);   // excluded → skipped

        Assert.Equal(65.00m, r.AppliedFloor);   // 70 would mean the excluded ratio contributed a floor
        Assert.Equal(65.00m, r.Score);
        Assert.Equal("High", r.Level);

        // The entry is still LISTED, still labelled off its placeholder, and still weightless — the
        // floor is the only place exclusion has to be honoured, and it is.
        var cobertura = Entry(r, "cobertura_intereses");
        Assert.True(cobertura.Excluded);
        Assert.Equal(0m, cobertura.EffectiveWeight);
        Assert.Equal("Critico", cobertura.Status);
    }

    // ── 6. The degenerate component-weight branch falls back to the DEFAULT weights ────────────
    //
    // weight_component_ratios = 0 and weight_component_macro = 0 (bcra 1.00, so the trio still sums
    // to 1 and clears the sum-to-1.0 rule) with BCRA EXCLUDED. denom = 0 + 0 = 0, so there is
    // nothing to renormalise over.
    //
    // The old guard fell back to the NOMINAL weights — the same two zeros — so every effective
    // weight ended at 0 and the composite collapsed to 0.00 / "Low": maximum apparent health for
    // any company at all. The fallback is now ScoringDefaults, valid by construction, so the
    // composite stays a real weighted average of the two components that survived.
    //
    //   denom 0 → defaults ratios 0.50, macro 0.15 ; dDenom = 0.65
    //     effWRatios = 0.50/0.65 = 0.769230…  → reported 0.7692
    //     effWMacro  = 0.15/0.65 = 0.230769…  → reported 0.2308   (effWBcra 0 — BCRA excluded)
    //     Score = 21.00*(0.50/0.65) + 30.00*(0.15/0.65) = (10.50 + 4.50)/0.65 = 15/0.65
    //           = 23.076923…  → 23.08 ; 23.08 <= 35 → "Low"
    //   No partial reaches the 100 trigger (v9), so no floor binds. Reverting the fallback to the nominal
    //   weights puts this back at 0.00 — a 23-point swing is what the assertion pins.
    //
    // SettingsService now rejects this config outright (see SettingsServiceScoringValidationTests);
    // this covers the second layer — a row already in the DB that predates that check.

    [Fact]
    public async Task Calculate_WhenRatioAndMacroWeightsAreBothZero_AndBcraExcluded_FallsBackToTheDefaultWeights()
    {
        var sut = await BuildAsync(
            ("weight_component_ratios", 0m),
            ("weight_component_bcra",   1.00m),
            ("weight_component_macro",  0m));

        var r = await Score(sut, BaselineRatios(), bcra: null, macro: MacroBenign());

        Assert.True(r.BcraExcluded);
        Assert.Equal(21.00m, r.ScoreRatios);   // the components are computed normally...
        Assert.Equal(30.00m, r.ScoreMacro);

        Assert.Equal(0.7692m, r.EffectiveWeightRatios);   // ...and weighted by the DEFAULT split
        Assert.Equal(0.0000m, r.EffectiveWeightBcra);     // excluded, as always on this path
        Assert.Equal(0.2308m, r.EffectiveWeightMacro);

        Assert.Equal(23.08m, r.Score);   // 0.00 would mean the nominal zeros were reused
        Assert.Equal("Low", r.Level);
        Assert.Equal(r.ScoreBeforeFloor, r.Score);   // no partial clears the 100 trigger (v9)
    }

    // ── 7. The CalculateAsync catch-all ────────────────────────────────────────────────────────
    //
    // Two rows colliding on (TenantId, ConfigKey) — the unique index exists in the Fluent config but
    // the in-memory provider does not enforce it — make LoadConfigAsync's ToDictionary throw. The
    // catch logs and returns the neutral result, which scoring never populated.
    //
    // entitySector is "Industria" deliberately: the neutral result hardcodes SectorProfile "General",
    // so a non-General input proves the fixture really went down the catch path.

    [Fact]
    public async Task Calculate_WhenConfigCannotBeLoaded_ReturnsTheNeutralResult()
    {
        var sut = await BuildFromRowsAsync(
            Row(Tenant, "weight_component_bcra", 0.35m),
            Row(Tenant, "weight_component_bcra", 0.40m));   // duplicate key → ToDictionary throws

        var r = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), entitySector: "Industria");

        Assert.Equal(50m, r.Score);
        Assert.Equal("Medium", r.Level);
        Assert.Equal(50m, r.ScoreRatios);
        Assert.Equal(50m, r.ScoreBcra);
        Assert.Equal(50m, r.ScoreMacro);
        Assert.Equal("General", r.SectorProfile);

        // Everything scoring would have filled in is left at its default — the engine never ran.
        Assert.Empty(r.Breakdown);
        Assert.Empty(r.CriticalRatios);
        Assert.Empty(r.NonComputableRatios);
        Assert.Equal(0m, r.AppliedFloor);
        Assert.Equal(0m, r.ScoreBeforeFloor);
        Assert.Equal(0m, r.RatiosSurvivingWeight);
        Assert.False(r.RatiosLowCoverageFallback);
        Assert.False(r.BcraExcluded);
        Assert.Null(r.BcraExclusionReason);

        // No cheques penalty either: nothing was evaluated, so the adjustment is 0 and the proportion
        // is null rather than a 0 that would read as "we measured, and nothing was recovered".
        Assert.Equal(0m, r.ChequesAdj);
        Assert.Null(r.ChequesProporcionRecuperada);

        // No algorithm ran, so no algorithm version is claimed. Stamping one here would make the
        // field useless for the audit question it exists to answer ("which engine produced this
        // score?") and would be indistinguishable from a genuine v2 computation.
        Assert.Null(r.ScoringAlgoVersion);
    }
}
