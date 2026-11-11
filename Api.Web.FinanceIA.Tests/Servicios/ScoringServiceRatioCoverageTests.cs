// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-01

using Api.Web.FinanceIA.Models.Financia;
using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.ScoringTestHarness;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Pins the <c>MinRatioSurvivingWeight = 0.50</c> decision in ScoringService — whether the surviving
/// ratios are renormalised or the engine falls back to the neutral behaviour.
/// <para>
/// GAP THIS FILLS: <see cref="ScoringServiceTests"/> pins 0.55 (renormalises) and 0.30 (falls back),
/// but never the BOUNDARY ITSELF. Flipping <c>&gt;=</c> to <c>&gt;</c> at that comparison would keep
/// every existing test green. The two cases below — exactly 0.50 and exactly 0.48 — bracket it as
/// tightly as the default weights allow.
/// </para>
/// <para>
/// The source comment above the constant names the clusters it is calibrated around: the four-null
/// cluster (0.55) survives, income-statement-only (~0.22) and liquidity-only (~0.30) do not. 0.55 and
/// 0.30 are already pinned; the 0.22 figure was asserted by nobody, so it is pinned here too — it
/// verifies as EXACTLY 0.22 (cobertura .15 + margen_neto .07), the only two ratios computable from a
/// profit-and-loss statement alone.
/// </para>
/// </summary>
public sealed class ScoringServiceRatioCoverageTests
{
    // ── 1. Surviving weight EXACTLY 0.50 → renormalises (the >= is inclusive) ───────────────────
    //
    // Computable: liquidez .20 + endeudamiento .20 + roe .10 = 0.50 exactly.
    // Excluded:   prueba .10 + leverage .10 + cobertura .15 + roa .08 + margen .07 = 0.50.
    //
    //   liquidez 1.40 → 10.00 ; endeudamiento 0.55 → 12.50 ; roe 4.0 → 60.00
    //   survivorWeighted = 10.00*.20 + 12.50*.20 + 60.00*.10 = 2.00 + 2.50 + 6.00 = 10.50
    //   scoreRatios      = 10.50 / 0.50 = 21.00
    //   Score            = 21.00*0.50 + 0*0.35 + 30*0.15 = 10.50 + 4.50 = 15.00 → "Low"

    [Fact]
    public async Task Calculate_WhenSurvivingWeightIsExactly050_Renormalises()
    {
        var sut = await BuildAsync();

        var ratios = new RatiosData
        {
            CurrentRatio     = 1.40,
            AcidTest         = null,
            DebtRatio        = 0.55,
            Leverage         = null,
            InterestCoverage = null,
            Roe              = 4.0,
            Roa              = null,
            NetMargin        = null,
        };

        var r = await Score(sut, ratios, BcraNoDebt(), MacroBenign());

        Assert.False(r.RatiosLowCoverageFallback);      // the boundary is INCLUSIVE
        Assert.Equal(0.5000m, r.RatiosSurvivingWeight);
        Assert.Equal(21.00m, r.ScoreRatios);
        Assert.Equal(15.00m, r.Score);
        Assert.Equal("Low", r.Level);

        Assert.Equal(
            new[] { "prueba_acida", "leverage", "cobertura_intereses", "roa", "margen_neto" },
            r.NonComputableRatios);

        // Survivors carry w / 0.50; the five excluded carry 0 and are flagged.
        Assert.Equal(0.4000m, Entry(r, "liquidez_corriente").EffectiveWeight);
        Assert.Equal(0.4000m, Entry(r, "endeudamiento").EffectiveWeight);
        Assert.Equal(0.2000m, Entry(r, "roe").EffectiveWeight);
        foreach (var k in new[] { "prueba_acida", "leverage", "cobertura_intereses", "roa", "margen_neto" })
        {
            Assert.True(Entry(r, k).Excluded);
            Assert.Equal(0m, Entry(r, k).EffectiveWeight);
        }
    }

    // ── 2. Surviving weight EXACTLY 0.48 → falls back ───────────────────────────────────────────
    //
    // The same shape as above with roa (.08) computable instead of roe (.10): 0.02 less coverage
    // flips the branch. Fallback == the historical behaviour, i.e. the five nulls scored 50 at FULL
    // nominal weight, nothing marked Excluded.
    //
    //   raw = 10.00*.20 + 50*.10 + 12.50*.20 + 50*.10 + 50*.15 + 50*.10 + 10.00*.08 + 50*.07
    //       = 2.00 + 5.00 + 2.50 + 5.00 + 7.50 + 5.00 + 0.80 + 3.50 = 31.30
    //   Score = 31.30*0.50 + 4.50 = 15.65 + 4.50 = 20.15 → "Low"
    //
    // Read together with case 1, the pair is the point: 0.02 of coverage moves the score 5.15 points.

    [Fact]
    public async Task Calculate_WhenSurvivingWeightIsJustBelow050_FallsBackToNeutral()
    {
        var sut = await BuildAsync();

        var ratios = new RatiosData
        {
            CurrentRatio     = 1.40,
            AcidTest         = null,
            DebtRatio        = 0.55,
            Leverage         = null,
            InterestCoverage = null,
            Roe              = null,
            Roa              = 7.0,
            NetMargin        = null,
        };

        var r = await Score(sut, ratios, BcraNoDebt(), MacroBenign());

        Assert.True(r.RatiosLowCoverageFallback);
        Assert.Equal(0.4800m, r.RatiosSurvivingWeight);
        Assert.Equal(31.30m, r.ScoreRatios);
        Assert.Equal(20.15m, r.Score);
        Assert.Equal("Low", r.Level);

        Assert.Equal(
            new[] { "prueba_acida", "leverage", "cobertura_intereses", "roe", "margen_neto" },
            r.NonComputableRatios);

        // NonComputableRatios is populated on this path too, yet NOTHING is marked Excluded and every
        // entry keeps its nominal weight. The two fields are not redundant — asserting only the first
        // would let a renormalise/fallback regression through.
        Assert.All(r.Breakdown.Values, e => Assert.False(e.Excluded));
        Assert.Equal(0.1000m, Entry(r, "roe").EffectiveWeight);
        Assert.Equal(50m, Entry(r, "roe").Score);
    }

    // ── 3. Income-statement-only — the 0.22 cluster the source comment names but nobody pinned ──
    //
    // Only cobertura_intereses (.15) and margen_neto (.07) are computable from a P&L alone = 0.22.
    //   raw = 50*.20 + 50*.10 + 50*.20 + 50*.10 + 10.00*.15 + 50*.10 + 50*.08 + 60.00*.07
    //       = 10.00 + 5.00 + 10.00 + 5.00 + 1.50 + 5.00 + 4.00 + 4.20 = 44.70
    //   Score = 44.70*0.50 + 4.50 = 22.35 + 4.50 = 26.85 → "Low"

    [Fact]
    public async Task Calculate_IncomeStatementOnlyCluster_IsExactly022_AndFallsBack()
    {
        var sut = await BuildAsync();

        var ratios = new RatiosData
        {
            InterestCoverage = 2.70,
            NetMargin        = 3.2,
        };

        var r = await Score(sut, ratios, BcraNoDebt(), MacroBenign());

        Assert.Equal(0.2200m, r.RatiosSurvivingWeight);   // the comment's "~0.22" is exact
        Assert.True(r.RatiosLowCoverageFallback);
        Assert.Equal(44.70m, r.ScoreRatios);
        Assert.Equal(26.85m, r.Score);
        Assert.Equal("Low", r.Level);
        Assert.Equal(6, r.NonComputableRatios.Count);
        Assert.All(r.Breakdown.Values, e => Assert.False(e.Excluded));
    }

    // ── 4. Ratio-level AND component-level renormalisation in the SAME call ─────────────────────
    //
    // The two renormalisations are independent mechanisms that had only ever been exercised apart.
    // Here cobertura is n/d (0.85 survives → ratios renormalise) AND BCRA is absent (component
    // weights renormalise over ratios+macro).
    //
    //   scoreRatios = 13.50 / 0.85           = 15.882352…      → reported 15.88
    //   effWRatios  = 0.50 / 0.65            = 0.769230…       → reported 0.7692
    //   effWMacro   = 0.15 / 0.65            = 0.230769…       → reported 0.2308
    //   Score = 13.5/0.85 * 0.5/0.65 + 30 * 0.15/0.65
    //         = 6.75/0.5525 + 4.5/0.65 = 12.217194… + 6.923076… = 19.140271… → 19.14 → "Low"
    //   (Both products are carried UNROUNDED into the sum — the reported 15.88 / 0.7692 are
    //    presentation only, so 15.88 * 0.7692 is NOT the right way to check this.)

    [Fact]
    public async Task Calculate_RatioAndBcraRenormalisation_BothApplyInOneCall()
    {
        var sut = await BuildAsync();

        var ratios = BaselineRatios();
        ratios.InterestCoverage = null;

        var r = await Score(sut, ratios, bcra: null, macro: MacroBenign());

        // ratio level
        Assert.Equal(15.88m, r.ScoreRatios);
        Assert.Equal(0.8500m, r.RatiosSurvivingWeight);
        Assert.False(r.RatiosLowCoverageFallback);
        Assert.Equal(new[] { "cobertura_intereses" }, r.NonComputableRatios);
        Assert.True(Entry(r, "cobertura_intereses").Excluded);

        // component level
        Assert.True(r.BcraExcluded);
        Assert.Equal(BcraExclusionReasons.NoBcraData, r.BcraExclusionReason);
        Assert.Equal(0.7692m, r.EffectiveWeightRatios);
        Assert.Equal(0.0000m, r.EffectiveWeightBcra);
        Assert.Equal(0.2308m, r.EffectiveWeightMacro);

        Assert.Equal(19.14m, r.Score);
        Assert.Equal("Low", r.Level);
    }
}
