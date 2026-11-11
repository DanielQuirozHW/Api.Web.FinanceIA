// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-14

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Behavioural tests for <see cref="Api.Web.FinanceIA.Servicios.ScoringService"/>'s treatment of
/// non-computable ratios: excluded from the composite and the surviving weights renormalised (the
/// per-ratio analogue of BCRA exclusion), with a minimum-coverage guard that falls back to today's
/// neutral behaviour when too little ratio-weight survives.
///
/// Run against the EF Core in-memory provider with an empty <c>ScoringConfigs</c> table, so every
/// weight/threshold falls back to <see cref="ScoringDefaults"/> (ratio weights sum to 1.00; component
/// weights 0.50/0.35/0.15). Expected partial scores use PartialScore's anchor behaviour
/// (value = ok → 0, warn → 50, danger → 100).
/// </summary>
public sealed class ScoringServiceTests
{
    private const string Tenant = "tenant-scoring";

    private static Api.Web.FinanceIA.Servicios.ScoringService BuildSut()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"scoring-{Guid.NewGuid()}")
            .Options;
        var db = new FinanciaDbContext(options);
        return new Api.Web.FinanceIA.Servicios.ScoringService(
            db, NullLogger<Api.Web.FinanceIA.Servicios.ScoringService>.Instance);
    }

    // BCRA with a reported debt situation of "none" scores 0 and is NOT excluded — a clean, fixed
    // BCRA component so the tests isolate the ratios composite.
    private static BcraData BcraNoDebt() =>
        new() { Deuda = new BcraDeudaData { Estado = BcraDeudaConsultaEstado.NoDebtReported } };

    // Macro present with sub-threshold inflation scores 30 (base, no penalty).
    private static BcraMacroData MacroBenign() => new() { InflacionMensual = 5.0 };

    private static Task<ScoringResult> Score(
        Api.Web.FinanceIA.Servicios.ScoringService sut,
        RatiosData ratios,
        BcraData? bcra = null,
        BcraMacroData? macro = null)
        => sut.CalculateAsync(Tenant, entitySector: null, ratios, bcra, macro, ventasMensuales: null, garantiasPorSgr: null, CancellationToken.None);

    private static RatioBreakdownEntry Entry(ScoringResult r, string key)
    {
        Assert.True(r.Breakdown.ContainsKey(key), $"breakdown must list '{key}'");
        return r.Breakdown[key];
    }

    // Sum of score × effectiveWeight over the entries that carry a weight — must reproduce ScoreRatios.
    private static decimal Reproduce(ScoringResult r) =>
        r.Breakdown.Values
            .Where(e => e.EffectiveWeight.HasValue)
            .Sum(e => e.Score * e.EffectiveWeight!.Value);

    // ── 1. Regression: all eight computable → composite is the raw weighted sum, unchanged ──────

    [Fact]
    public async Task Calculate_WhenAllEightComputable_CompositeIsRawWeightedSum_Unchanged()
    {
        var sut = BuildSut();

        // Anchor values → partials: liq 0, prueba 100, endeud 0, lev 100, cob 50, roe 50, roa 0, margen 100.
        // Composite = 0·.20 + 100·.10 + 0·.20 + 100·.10 + 50·.15 + 50·.10 + 0·.08 + 100·.07 = 39.50
        var ratios = new RatiosData
        {
            CurrentRatio     = 1.50,  // ok    → 0
            AcidTest         = 0.50,  // danger → 100
            DebtRatio        = 0.50,  // ok    → 0
            Leverage         = 5.67,  // danger → 100 (v9: leverage DANGER moved 2.50 → 5.67, the endeudamiento-0.85 equivalent)
            InterestCoverage = 1.50,  // warn  → 50
            Roe              = 5.0,   // warn  → 50
            Roa              = 8.0,   // ok    → 0
            NetMargin        = 0.0,   // danger → 100
        };

        var r = await Score(sut, ratios, BcraNoDebt(), macro: null);

        Assert.Equal(39.50m, r.ScoreRatios);
        Assert.False(r.RatiosLowCoverageFallback);
        Assert.Empty(r.NonComputableRatios);
        Assert.All(r.Breakdown.Values, e => Assert.False(e.Excluded));
        // Effective weights equal the nominal weights (no renormalisation).
        Assert.Equal(0.15m, Entry(r, "cobertura_intereses").EffectiveWeight);
        Assert.Equal(0.20m, Entry(r, "liquidez_corriente").EffectiveWeight);
        Assert.Equal(1.00m, r.RatiosSurvivingWeight);
        Assert.Equal(r.ScoreRatios, Math.Round(Reproduce(r), 2));
    }

    // ── 2. One non-computable ratio → excluded, survivors renormalised, still listed ────────────

    [Fact]
    public async Task Calculate_WhenOneRatioNonComputable_ExcludesAndRenormalises_StillListed()
    {
        var sut = BuildSut();

        // Same as the regression set but interest coverage n/d. Survivors (weight 0.85):
        // liq 0, prueba 100, endeud 0, lev 100, roe 50, roa 0, margen 100.
        // survivorWeighted = 100·.10 + 100·.10 + 50·.10 + 100·.07 = 32 ; composite = 32/0.85 = 37.65
        var ratios = new RatiosData
        {
            CurrentRatio     = 1.50,
            AcidTest         = 0.50,
            DebtRatio        = 0.50,
            Leverage         = 5.67,   // v9: the new leverage DANGER anchor (was 2.50)
            InterestCoverage = null,   // n/d
            Roe              = 5.0,
            Roa              = 8.0,
            NetMargin        = 0.0,
        };

        var r = await Score(sut, ratios, BcraNoDebt(), macro: null);

        Assert.Equal(Math.Round(32m / 0.85m, 2), r.ScoreRatios); // 37.65
        Assert.False(r.RatiosLowCoverageFallback);
        Assert.Equal(new[] { "cobertura_intereses" }, r.NonComputableRatios);
        Assert.Equal(0.85m, r.RatiosSurvivingWeight);

        var cob = Entry(r, "cobertura_intereses");
        Assert.True(cob.Excluded);
        Assert.Equal(0m, cob.EffectiveWeight);
        Assert.Equal(50m, cob.Score); // neutral placeholder kept, but Excluded/EffectiveWeight disambiguate it

        // Surviving effective weights are renormalised and sum to 1.
        var survivorWeight = r.Breakdown.Values.Where(e => !e.Excluded).Sum(e => e.EffectiveWeight!.Value);
        Assert.Equal(1.0m, survivorWeight, 3); // 4-dp weight rounding: survivors sum to ~0.9999
        Assert.Equal(Math.Round(0.20m / 0.85m, 4), Entry(r, "liquidez_corriente").EffectiveWeight);
        // Composite reproducible from the persisted per-ratio weights, within 4-dp weight rounding.
        Assert.True(Math.Abs(r.ScoreRatios - Reproduce(r)) <= 0.05m);
    }

    // ── 3. Coverage just above 0.50 → renormalise the realistic four-null cluster ───────────────

    [Fact]
    public async Task Calculate_WhenFourNullClusterLeaves055_Renormalises()
    {
        var sut = BuildSut();

        // Null {prueba_acida, leverage, cobertura_intereses, roe} = 0.45 excluded → 0.55 survives ≥ 0.50.
        // Survivors: liq 0 (.20), endeud 50 (.20), roa 0 (.08), margen 50 (.07).
        // survivorWeighted = 50·.20 + 50·.07 = 13.5 ; composite = 13.5/0.55 = 24.55
        var ratios = new RatiosData
        {
            CurrentRatio     = 1.50,  // 0
            AcidTest         = null,
            DebtRatio        = 0.70,  // warn → 50
            Leverage         = null,
            InterestCoverage = null,
            Roe              = null,
            Roa              = 8.0,   // 0
            NetMargin        = 4.0,   // warn → 50
        };

        var r = await Score(sut, ratios, BcraNoDebt(), macro: null);

        Assert.False(r.RatiosLowCoverageFallback);
        Assert.Equal(0.55m, r.RatiosSurvivingWeight);
        Assert.Equal(Math.Round(13.5m / 0.55m, 2), r.ScoreRatios); // 24.55
        Assert.Equal(
            new[] { "prueba_acida", "leverage", "cobertura_intereses", "roe" }.OrderBy(k => k),
            r.NonComputableRatios.OrderBy(k => k));

        foreach (var k in new[] { "prueba_acida", "leverage", "cobertura_intereses", "roe" })
        {
            Assert.True(Entry(r, k).Excluded);
            Assert.Equal(0m, Entry(r, k).EffectiveWeight);
        }
        var survivorWeight = r.Breakdown.Values.Where(e => !e.Excluded).Sum(e => e.EffectiveWeight!.Value);
        Assert.Equal(1.0m, survivorWeight, 3); // 4-dp weight rounding: survivors sum to ~0.9999
        Assert.Equal(0m, r.AppliedFloor); // no survivor is critical
        Assert.True(Math.Abs(r.ScoreRatios - Reproduce(r)) <= 0.05m);
    }

    // ── 4. Coverage below 0.50 → neutral fallback (today's behaviour), recorded not silent ──────

    [Fact]
    public async Task Calculate_WhenCoverageBelowMinimum_FallsBackToNeutral_AndFlags()
    {
        var sut = BuildSut();

        // Liquidity-only: only current + acid computable → 0.30 surviving < 0.50 → fallback.
        // Fallback == today's raw weighted sum with the six nulls scored 50 at full nominal weight:
        // liq 0·.20 + prueba 100·.10 + (endeud+lev+cob+roe+roa+margen each 50) = 10 + 45 = 45.00
        var ratios = new RatiosData
        {
            CurrentRatio     = 1.50,  // 0
            AcidTest         = 0.50,  // 100
            DebtRatio        = null,
            Leverage         = null,
            InterestCoverage = null,
            Roe              = null,
            Roa              = null,
            NetMargin        = null,
        };

        var r = await Score(sut, ratios, BcraNoDebt(), macro: null);

        Assert.True(r.RatiosLowCoverageFallback);
        Assert.Equal(45.00m, r.ScoreRatios);
        Assert.Equal(0.30m, r.RatiosSurvivingWeight);
        Assert.Equal(6, r.NonComputableRatios.Count);
        Assert.Contains("endeudamiento", r.NonComputableRatios);

        // Fallback keeps today's breakdown: nothing marked excluded, nominal weights, nulls scored 50.
        Assert.All(r.Breakdown.Values, e => Assert.False(e.Excluded));
        Assert.Equal(0.20m, Entry(r, "endeudamiento").EffectiveWeight);
        Assert.Equal(50m, Entry(r, "endeudamiento").Score);
        Assert.Equal(r.ScoreRatios, Math.Round(Reproduce(r), 2));
    }

    // ── 5. An excluded ratio never removes a floor triggered by a genuinely critical one ────────

    [Fact]
    public async Task Calculate_WhenExcludedRatio_DoesNotRemoveCriticalFloor()
    {
        var sut = BuildSut();

        // Interest coverage n/d (excluded, 0.85 survives → renormalise); debt ratio genuinely
        // critical (danger → 100 ≥ the trigger, 100 since v9). Floor for endeudamiento is 65.
        var ratios = new RatiosData
        {
            CurrentRatio     = 2.00,  // 0
            AcidTest         = 1.50,  // 0
            DebtRatio        = 0.85,  // danger → 100 (critical)
            Leverage         = 0.50,  // 0
            InterestCoverage = null,  // excluded
            Roe              = 20.0,  // 0
            Roa              = 10.0,  // 0
            NetMargin        = 15.0,  // 0
        };

        var r = await Score(sut, ratios, BcraNoDebt(), macro: null);

        Assert.Contains("endeudamiento", r.CriticalRatios);
        Assert.DoesNotContain("cobertura_intereses", r.CriticalRatios); // excluded → cannot be critical
        Assert.True(Entry(r, "cobertura_intereses").Excluded);
        Assert.Equal(65.00m, r.AppliedFloor);
        Assert.True(r.ScoreBeforeFloor < r.AppliedFloor);
        Assert.Equal(65.00m, r.Score);     // floor applied (raised from ~19.26)
        Assert.Equal("High", r.Level);     // 60 < 65 <= 80
    }

    // ── 6. Biotrust: reproduce the stated figures exactly, verified against ScoreScale.ToNosis ──
    //
    // Interest coverage is n/d (RECPAM-consolidated). With it scored at the neutral 50 the ratios
    // composite is 21 internal (790 NOSIS) and the final score is 850 NOSIS. Excluding it and
    // renormalising the survivors is the change under test.

    [Fact]
    public async Task Calculate_Biotrust_Before_ReproducesTodaysComposite21_Final850()
    {
        var sut = BuildSut();

        // Survivors give endeud 50 (.20) + margen 50 (.07) = 13.5 ; cobertura 50·.15 = 7.5 → composite 21.
        var ratios = BiotrustRatios(interestCoverage: 1.50); // warn → 50

        var r = await Score(sut, ratios, BcraNoDebt(), MacroBenign());

        Assert.Equal(21.00m, r.ScoreRatios);
        Assert.Equal(790, Api.Web.FinanceIA.Servicios.ScoreScale.ToNosis(r.ScoreRatios));
        Assert.False(r.RatiosLowCoverageFallback);
        Assert.Empty(r.NonComputableRatios);
        Assert.Equal(15.00m, r.Score);
        Assert.Equal(850, Api.Web.FinanceIA.Servicios.ScoreScale.ToNosis(r.Score));
    }

    [Fact]
    public async Task Calculate_Biotrust_After_ExcludesInterestCoverage_RaisesScore()
    {
        var sut = BuildSut();

        var ratios = BiotrustRatios(interestCoverage: null); // n/d

        var r = await Score(sut, ratios, BcraNoDebt(), MacroBenign());

        // New composite = 13.5 / 0.85 = 15.88 internal → 840 NOSIS (up from 790).
        Assert.Equal(15.88m, r.ScoreRatios);
        Assert.Equal(840, Api.Web.FinanceIA.Servicios.ScoreScale.ToNosis(r.ScoreRatios));

        // New final = 15.88.. · 0.50 + 0 · 0.35 + 30 · 0.15 = 12.44 internal → 880 NOSIS (up from 850).
        Assert.Equal(12.44m, r.Score);
        Assert.Equal(880, Api.Web.FinanceIA.Servicios.ScoreScale.ToNosis(r.Score));

        var cob = Entry(r, "cobertura_intereses");
        Assert.True(cob.Excluded);
        Assert.Equal(0m, cob.EffectiveWeight);
        Assert.Equal(new[] { "cobertura_intereses" }, r.NonComputableRatios);

        // Effective per-ratio weights = each survivor wᵢ / 0.85.
        Assert.Equal(Math.Round(0.20m / 0.85m, 4), Entry(r, "endeudamiento").EffectiveWeight); // 0.2353
        Assert.Equal(Math.Round(0.07m / 0.85m, 4), Entry(r, "margen_neto").EffectiveWeight);   // 0.0824
        var survivorWeight = r.Breakdown.Values.Where(e => !e.Excluded).Sum(e => e.EffectiveWeight!.Value);
        Assert.Equal(1.0m, survivorWeight, 3); // 4-dp weight rounding: survivors sum to ~0.9999
        Assert.True(Math.Abs(r.ScoreRatios - Reproduce(r)) <= 0.05m);
    }

    // liq 0, prueba 0, endeud 50, lev 0, roe 0, roa 0, margen 50, cobertura per argument.
    private static RatiosData BiotrustRatios(double? interestCoverage) => new()
    {
        CurrentRatio     = 1.50,  // ok   → 0
        AcidTest         = 1.00,  // ok   → 0
        DebtRatio        = 0.70,  // warn → 50
        Leverage         = 1.00,  // ok   → 0
        InterestCoverage = interestCoverage,
        Roe              = 15.0,  // ok   → 0
        Roa              = 8.0,   // ok   → 0
        NetMargin        = 4.0,   // warn → 50
    };

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  BCRA exclusion reason — and the calibration pin that guards it
    //
    //  BcraExclusionReason splits what was ONE compound condition
    //  (`bcraData?.Deuda is null || estado == LookupFailed`) into three branches, so each can carry
    //  its own literal. That is a structural edit to the branch the score depends on, and these
    //  tests exist to prove it moved NOTHING. Every expected value below is hand-computed from
    //  ScoringDefaults and pinned exactly.
    //
    //  If one of these fails, the calibration changed. That is a release blocker, not a test to
    //  update — how much a degraded analysis should be penalised is a pending business decision.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    // Deliberately its own fixture rather than a reuse of BiotrustRatios(): a calibration pin must
    // not silently move if someone re-tunes the Biotrust scenario. The values coincide today, which
    // is useful — the already-green Biotrust tests independently confirm scoreRatios == 21.00.
    //
    // All eight computable, so ratio renormalisation is inert and cannot mask BCRA drift; no partial
    // reaches the critical trigger (100 since v9), so no floor clamps the composite and the pins stay falsifiable.
    //
    //   liquidez_corriente  1.50 = ok    → 0    (w .20)
    //   prueba_acida        1.00 = ok    → 0    (w .10)
    //   endeudamiento       0.70 = warn  → 50   (w .20) → 10.00
    //   leverage            1.00 = ok    → 0    (w .10)
    //   cobertura_intereses 1.50 = warn  → 50   (w .15) →  7.50
    //   roe                15.0  = ok    → 0    (w .10)
    //   roa                 8.0  = ok    → 0    (w .08)
    //   margen_neto         4.0  = warn  → 50   (w .07) →  3.50
    //                                          scoreRatios = 21.00
    private static RatiosData PinnedRatios() => new()
    {
        CurrentRatio     = 1.50,
        AcidTest         = 1.00,
        DebtRatio        = 0.70,
        Leverage         = 1.00,
        InterestCoverage = 1.50,
        Roe              = 15.0,
        Roa              = 8.0,
        NetMargin        = 4.0,
    };

    // DebtFound with situación 3 → scoreBcra 45. HistorialSituaciones is left at its default empty
    // list on purpose: the trend block needs Count >= 2, so it never fires and the pinned figures
    // carry NO DateTime.UtcNow dependency (ScoringService's trend cutoff) — this test cannot rot.
    private static BcraData BcraDebtFound(int situacionMaxima) =>
        new()
        {
            Deuda = new BcraDeudaData
            {
                Estado          = BcraDeudaConsultaEstado.DebtFound,
                SituacionMaxima = situacionMaxima,
            }
        };

    private static BcraData BcraNoDeudaBlock() => new();

    private static BcraData BcraLookupFailed() =>
        new() { Deuda = new BcraDeudaData { Estado = BcraDeudaConsultaEstado.LookupFailed } };

    // Guards the pins themselves: if a future threshold tweak made a partial cross 85, the floor
    // would clamp the score and the component assertions would stop being falsifiable.
    private static void AssertRatioMachineryInert(ScoringResult r)
    {
        Assert.False(r.RatiosLowCoverageFallback);
        Assert.Empty(r.NonComputableRatios);
        Assert.Equal(1.00m, r.RatiosSurvivingWeight);
        Assert.Empty(r.CriticalRatios);
        Assert.Equal(0m, r.AppliedFloor);
    }

    // ── 7. Calibration pin — BCRA INCLUDED (DebtFound, situación 3) ─────────────────────────────
    //
    //   21.00·0.50 + 45·0.35 + 30·0.15 = 10.5000 + 15.7500 + 4.5000 = 30.75 ; floor 0 ; ≤ 35 → "Low"

    [Fact]
    public async Task Calculate_BcraIncluded_PinsCalibration_AndReasonIsNull()
    {
        var sut = BuildSut();

        var r = await Score(sut, PinnedRatios(), BcraDebtFound(situacionMaxima: 3), MacroBenign());

        AssertRatioMachineryInert(r);

        // ── Calibration pin. Do not "update" these. ──
        Assert.Equal(21.00m,  r.ScoreRatios);
        Assert.Equal(45.00m,  r.ScoreBcra);
        Assert.Equal(30.00m,  r.ScoreMacro);
        Assert.Equal(0.5000m, r.EffectiveWeightRatios);
        Assert.Equal(0.3500m, r.EffectiveWeightBcra);
        Assert.Equal(0.1500m, r.EffectiveWeightMacro);
        Assert.Equal(30.75m,  r.ScoreBeforeFloor);
        Assert.Equal(30.75m,  r.Score);
        Assert.Equal("Low",   r.Level);

        // Trend block never fired (empty historial) — so the pins above have no UtcNow dependency.
        Assert.Equal(0,  r.BcraTrendDelta);
        Assert.Equal(0m, r.BcraTrendAdj);

        // NOSIS is a SECONDARY assertion: ToNosis rounds the motor score to a whole number first,
        // so it has a ±0.5 dead band and would not catch a small drift on its own.
        Assert.Equal(790, Api.Web.FinanceIA.Servicios.ScoreScale.ToNosis(r.ScoreRatios));
        Assert.Equal(690, Api.Web.FinanceIA.Servicios.ScoreScale.ToNosis(r.Score));

        Assert.False(r.BcraExcluded);
        Assert.Null(r.BcraExclusionReason);
    }

    // ── 8. Calibration pin — BCRA EXCLUDED, all three causes, one shared calibration ────────────
    //
    //   denom      = 0.50 + 0.15 = 0.65
    //   effWRatios = 0.50/0.65 = 0.769230769…  → Round(4) = 0.7692
    //   effWMacro  = 0.15/0.65 = 0.230769230…  → Round(4) = 0.2308
    //   scoreFinal = 21.00·0.769230769… + 0·0 + 30·0.230769230… = 23.076923076… → Round(2) = 23.08
    //
    //   23.08 is safe to pin exactly: the nearest 2-dp midpoints are 23.075 / 23.085, and the value
    //   sits 1.9e-3 from the closer one against a decimal-division residue of ~1e-26.
    //
    //   floor 0 ; 23.08 ≤ 35 → "Low"
    //
    // Asserting the SAME numbers for all three causes is what proves no branch of the split drifted.

    [Theory]
    [InlineData(BcraExclusionReasons.NoBcraData)]
    [InlineData(BcraExclusionReasons.NoDeudaBlock)]
    [InlineData(BcraExclusionReasons.LookupFailed)]
    public async Task Calculate_BcraExcluded_AllThreeCauses_ShareOneCalibration_AndCarryTheirReason(
        string expectedReason)
    {
        var sut = BuildSut();

        BcraData? bcra = expectedReason switch
        {
            BcraExclusionReasons.NoBcraData   => null,
            BcraExclusionReasons.NoDeudaBlock => BcraNoDeudaBlock(),
            BcraExclusionReasons.LookupFailed => BcraLookupFailed(),
            _ => throw new ArgumentOutOfRangeException(nameof(expectedReason), expectedReason, null)
        };

        var r = await Score(sut, PinnedRatios(), bcra, MacroBenign());

        AssertRatioMachineryInert(r);

        // ── Calibration pin. Identical for all three causes — that is the point. ──
        Assert.Equal(21.00m,  r.ScoreRatios);   // exclusion never touches the ratios composite
        Assert.Equal(0m,      r.ScoreBcra);     // forced to 0 by the `bcraExcluded ? 0m :` ternary
        Assert.Equal(30.00m,  r.ScoreMacro);
        Assert.Equal(0.7692m, r.EffectiveWeightRatios);
        Assert.Equal(0.0000m, r.EffectiveWeightBcra);
        Assert.Equal(0.2308m, r.EffectiveWeightMacro);
        Assert.Equal(1.0000m, r.EffectiveWeightRatios + r.EffectiveWeightMacro);
        Assert.Equal(23.08m,  r.ScoreBeforeFloor);
        Assert.Equal(23.08m,  r.Score);
        Assert.Equal("Low",   r.Level);

        Assert.Equal(0,  r.BcraTrendDelta);
        Assert.Equal(0m, r.BcraTrendAdj);

        // ToNosis(r.ScoreBcra) is deliberately NOT asserted: it would be ToNosis(0) == 1000, i.e.
        // "BCRA perfectly healthy" — a presentation artefact of the exclusion, not a fact. See the
        // guard rail in ClaudeService.BuildNarrativePrompt.
        Assert.Equal(790, Api.Web.FinanceIA.Servicios.ScoreScale.ToNosis(r.ScoreRatios));
        Assert.Equal(770, Api.Web.FinanceIA.Servicios.ScoreScale.ToNosis(r.Score));

        Assert.True(r.BcraExcluded);
        Assert.Equal(expectedReason, r.BcraExclusionReason);
    }

    // ── 9. NoDebtReported is INCLUDED — the case ScoreBcra alone cannot distinguish ─────────────
    //
    //   scoreBcra is 0 here (optimal) AND 0 on every exclusion path. Only BcraExcluded /
    //   BcraExclusionReason / EffectiveWeightBcra tell them apart — the whole reason for the field.
    //
    //   21.00·0.50 + 0·0.35 + 30·0.15 = 10.50 + 4.50 = 15.00 → "Low" → 850 NOSIS

    [Fact]
    public async Task Calculate_BcraNoDebtReported_IsIncluded_AndReasonIsNull()
    {
        var sut = BuildSut();

        var r = await Score(sut, PinnedRatios(), BcraNoDebt(), MacroBenign());

        Assert.False(r.BcraExcluded);
        Assert.Null(r.BcraExclusionReason);

        Assert.Equal(0m,      r.ScoreBcra);           // optimal, NOT excluded
        Assert.Equal(0.3500m, r.EffectiveWeightBcra); // the discriminator: full nominal BCRA weight
        Assert.Equal(0.5000m, r.EffectiveWeightRatios);
        Assert.Equal(0.1500m, r.EffectiveWeightMacro);
        Assert.Equal(15.00m,  r.Score);
        Assert.Equal("Low",   r.Level);
        Assert.Equal(850, Api.Web.FinanceIA.Servicios.ScoreScale.ToNosis(r.Score));
    }

    // ── 10. An unrecognised estado still falls through to DebtFound ─────────────────────────────

    [Fact]
    public async Task Calculate_WhenEstadoOutOfRange_StillTreatedAsDebtFound_NotExcluded()
    {
        var sut = BuildSut();

        var bcra = new BcraData
        {
            Deuda = new BcraDeudaData
            {
                Estado          = (BcraDeudaConsultaEstado)99,  // corrupt or future persisted value
                SituacionMaxima = 3
            }
        };

        var r = await Score(sut, PinnedRatios(), bcra, MacroBenign());

        // Documents today's behaviour verbatim: the final `else // DebtFound` is a catch-all, so an
        // unrecognised estado is scored as DebtFound and BCRA is NOT excluded. Whether that is the
        // right product behaviour is a separate question — this test only guarantees it did not
        // change under the exclusion-reason split, which is exactly where a naive switch-based
        // refactor would have drifted it.
        Assert.False(r.BcraExcluded);
        Assert.Null(r.BcraExclusionReason);
        Assert.Equal(45.00m, r.ScoreBcra);
        Assert.Equal(30.75m, r.Score);
    }

    // ── 11. Cross-cutting invariant: reason non-null ⟺ BCRA excluded ────────────────────────────

    [Fact]
    public async Task Calculate_BcraExclusionReason_IsNonNull_ExactlyWhenBcraExcluded()
    {
        var sut = BuildSut();

        string[] known =
        [
            BcraExclusionReasons.NoBcraData,
            BcraExclusionReasons.NoDeudaBlock,
            BcraExclusionReasons.LookupFailed
        ];

        BcraData?[] cases =
        [
            null,                // exclude → NoBcraData
            BcraNoDeudaBlock(),  // exclude → NoDeudaBlock
            BcraLookupFailed(),  // exclude → LookupFailed
            BcraNoDebt(),        // include → null
            BcraDebtFound(3),    // include → null
        ];

        foreach (var bcra in cases)
        {
            var r = await Score(sut, PinnedRatios(), bcra, MacroBenign());

            Assert.Equal(r.BcraExcluded, r.BcraExclusionReason is not null);

            if (r.BcraExcluded)
                Assert.Contains(r.BcraExclusionReason!, known);
        }
    }
}
