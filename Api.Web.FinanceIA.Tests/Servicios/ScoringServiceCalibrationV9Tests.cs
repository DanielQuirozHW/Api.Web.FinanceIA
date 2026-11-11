// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-24

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.ScoringTestHarness;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Pins scoring algorithm version 9: a ratio is critical only at its DANGER threshold (default
/// <c>critical_floor_trigger_threshold</c> 100, was 85), leverage thresholds are the endeudamiento
/// anchors restated as L = E/(1−E) (1.00 / 2.33 / 5.67, were 1.00 / 1.50 / 2.50), and the engine records
/// the floor configuration it read.
/// <para>
/// The case that prompted it is pinned first: a Comercio dealer whose leverage 2.29 — below its own
/// DANGER, and the equivalent of an endeudamiento that read only "Atención" — was floored from a
/// composite of 18.28 to 65. Every "old" figure below is reproduced by seeding the pre-v9 values as
/// ScoringConfig rows (<see cref="PreV9Config"/>), which is also what proves that a tenant still
/// carrying those rows keeps today's behaviour: the defaults moved, the arithmetic did not.
/// </para>
/// </summary>
public sealed class ScoringServiceCalibrationV9Tests
{
    /// <summary>The three rows that reproduce the version-8 defaults this version moved.</summary>
    private static readonly (string Key, decimal Value)[] PreV9Config =
    [
        ("critical_floor_trigger_threshold", 85m),
        ("threshold_leverage_warn",          1.50m),
        ("threshold_leverage_danger",        2.50m),
    ];

    // CasoComercioConcesionario — a Comercio dealer, anonymised (the case that prompted v9).
    // Cobertura n/d → excluded, survivors (weight 0.85) renormalised.
    //
    //   liquidez_corriente  1.51  >= ok 1.50                         →  0.00        (w .20)
    //   prueba_acida        0.94  (1.00-0.94)/(1.00-0.70)*50          → 10.00        (w .10)
    //   endeudamiento       0.70  == warn                             → 50.00        (w .20)
    //   leverage            2.29  v9: (2.29-1.00)/(2.33-1.00)*50      → 48.4962…     (w .10)
    //                             v8: 50+(2.29-1.50)/(2.50-1.50)*50   → 89.50
    //   roe                 22.18 >= ok 15                            →  0.00        (w .10)
    //   roa                 6.75  (8-6.75)/(8-3)*50                   → 12.50        (w .08)
    //   margen_neto         5.76  (10-5.76)/(10-4)*50                 → 35.3333…     (w .07)
    //
    //   v9 ratios = (1 + 10 + 4.84962 + 1 + 2.47333)/0.85 = 22.7329 → composite 22.7329*.5 + 0 + 30*.15 = 15.87
    //   v8 ratios = (1 + 10 + 8.95    + 1 + 2.47333)/0.85 = 27.5569 → composite 18.28 → leverage critical → 65
    private static RatiosData CasoComercioConcesionario() => new()
    {
        CurrentRatio     = 1.51,
        AcidTest         = 0.94,
        DebtRatio        = 0.70,
        Leverage         = 2.29,
        InterestCoverage = null,
        Roe              = 22.18,
        Roa              = 6.75,
        NetMargin        = 5.76,
    };

    // ── 1. The real case ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CasoComercioConcesionario_UnderV9Defaults_IsNotCritical_AndTakesNoFloor()
    {
        var sut = await BuildAsync();

        var r = await Score(sut, CasoComercioConcesionario(), BcraDebtFound(1), MacroBenign(), entitySector: "Comercio");

        // 48.50 < 50 → "Saludable", not "Atencion": 2.29 sits just under the new warn (2.33), exactly as
        // its endeudamiento equivalent 2.29/3.29 = 0.696 sits just under 0.70. The declared
        // endeudamiento 0.70 is that figure rounded, and lands ON warn (50, "Atencion"). One step of
        // rounding apart, no longer 40 partial points and a critical floor apart.
        Assert.Equal(48.50m, Entry(r, "leverage").Score);
        Assert.Equal("Saludable", Entry(r, "leverage").Status);
        Assert.Equal(50.00m, Entry(r, "endeudamiento").Score);
        Assert.Equal("Atencion", Entry(r, "endeudamiento").Status);
        Assert.Empty(r.CriticalRatios);
        Assert.Equal(0m, r.AppliedFloor);

        Assert.Equal(22.73m, r.ScoreRatios);
        Assert.Equal(0m,     r.ScoreBcra);
        Assert.Equal(30m,    r.ScoreMacro);
        Assert.Equal(15.87m, r.ScoreBeforeFloor);
        Assert.Equal(15.87m, r.Score);
        Assert.Equal("Low",  r.Level);
        Assert.Equal(840,    ScoreScale.ToNosis(r.Score));
        Assert.Equal("11",    r.ScoringAlgoVersion);   // v11: the acid-test floor needs a weak current ratio
    }

    [Fact]
    public async Task CasoComercioConcesionario_UnderThePreV9Config_KeepsTodaysFloor()
    {
        var sut = await BuildAsync(PreV9Config);

        var r = await Score(sut, CasoComercioConcesionario(), BcraDebtFound(1), MacroBenign(), entitySector: "Comercio");

        Assert.Equal(89.50m, Entry(r, "leverage").Score);
        Assert.Equal("Critico", Entry(r, "leverage").Status);
        Assert.Equal(new[] { "leverage" }, r.CriticalRatios);
        Assert.Equal(27.56m, r.ScoreRatios);
        Assert.Equal(18.28m, r.ScoreBeforeFloor);
        Assert.Equal(65.00m, r.AppliedFloor);
        Assert.Equal(65.00m, r.Score);
        Assert.Equal("High", r.Level);
        Assert.Equal(350,    ScoreScale.ToNosis(r.Score));
    }

    // The MIXED state 068 documents: a tenant that saved the Settings page holds leverage rows at the v8
    // values but NO trigger row, because BuildDto never exposes the trigger. At the deploy it takes the
    // v9 trigger (100) while keeping leverage 1.50 / 2.50 — so the same 89.5 partial is no longer
    // critical, and the 18.28 composite stands instead of flooring to 65.
    [Fact]
    public async Task CasoComercioConcesionario_InTheMixedState_TakesTheNewTrigger_ButKeepsTheOldLeverage()
    {
        var sut = await BuildAsync(
            ("threshold_leverage_ok",     1.00m),
            ("threshold_leverage_warn",   1.50m),
            ("threshold_leverage_danger", 2.50m));

        var r = await Score(sut, CasoComercioConcesionario(), BcraDebtFound(1), MacroBenign(), entitySector: "Comercio");

        Assert.Equal(100m,     r.CriticalFloorTrigger);
        Assert.Equal(89.50m,   Entry(r, "leverage").Score);
        Assert.Equal("Atencion", Entry(r, "leverage").Status);
        Assert.Empty(r.CriticalRatios);
        Assert.Equal(18.28m,   r.Score);
        Assert.Equal(820,      ScoreScale.ToNosis(r.Score));
    }

    // The LUFE drop on top, measured on the SINGLE-MONTH basis: only the two endpoint months are in
    // the series, so the three-month window cannot form. CPI 10 %/month over the twelve months makes
    // the deflator exactly 1.1^12 = 3.138428376721, and the recent month is 0.706 × that of the base,
    // so the real variation is exactly −0.294. The ramp (inicio −0.10, plena −0.35, 10 points) gives
    // 10 × (−0.10 − (−0.294)) / 0.25 = 7.76, added to the unfloored composite: 15.87 + 7.76 = 23.63.
    [Fact]
    public async Task CasoComercioConcesionario_WithARealSalesDrop_ScoresCompositePlusRamp_WithNoFloor()
    {
        var sut = await BuildAsync();

        var ventas = Ventas(("202501", 100_000m), ("202601", 221_573.0433965026m));
        var macro  = MacroConIpc(IpcPlano(10, "202601", 12));

        var r = await Score(sut, CasoComercioConcesionario(), BcraDebtFound(1), macro, entitySector: "Comercio", ventas: ventas);

        Assert.Equal("interanual", r.VentasBase);
        Assert.True(r.VentasVariacionEsReal);
        Assert.Equal(-0.2940m, r.VentasVariacion);

        Assert.Empty(r.CriticalRatios);
        Assert.Equal(0m,      r.AppliedFloor);
        Assert.Equal(15.87m,  r.ScoreBeforeFloor);
        Assert.Equal(7.76m,   r.VentasAdj);
        Assert.Equal(23.63m,  r.Score);
        Assert.Equal(15.87m + 7.76m, r.Score);
    }

    // ── 2. ROE: critical means negative (or zero), not "below 1.5 %" ──────────────────────────────
    //
    // HealthyRatios holds every other partial at 0, so scoreRatios is ROE's partial × 0.10 and the
    // composite is that × 0.50 + 4.50. ROE thresholds 15 / 5 / 0:
    //   1.0  → 50+(5-1)/5*50 = 90  → "Atencion" under v9 (it was "Critico" at the 85 trigger) → 9.00
    //   0.0  → danger → 100 → critical → floor 55 → 55.00
    //   −0.1 → beyond danger → 100 → critical → floor 55 → 55.00

    [Theory]
    [InlineData(1.0,  90.0,  "Atencion", false,  9.00)]
    [InlineData(0.0,  100.0, "Critico",  true,  55.00)]
    [InlineData(-0.1, 100.0, "Critico",  true,  55.00)]
    public async Task Roe_IsCriticalOnlyAtOrBelowZero(
        double roe, double expectedPartial, string expectedStatus, bool expectedCritical, double expectedScore)
    {
        var sut = await BuildAsync();

        var ratios = HealthyRatios();
        ratios.Roe = roe;

        var r = await Score(sut, ratios, BcraNoDebt(), MacroBenign());

        Assert.Equal((decimal)expectedPartial, Entry(r, "roe").Score);
        Assert.Equal(expectedStatus, Entry(r, "roe").Status);
        Assert.Equal(expectedCritical, r.CriticalRatios.Contains("roe"));
        Assert.Equal(expectedCritical ? 55.00m : 0m, r.AppliedFloor);
        Assert.Equal((decimal)expectedScore, r.Score);
    }

    [Fact]
    public async Task Roe_OnePercent_WasCriticalUnderThePreV9Config()
    {
        var sut = await BuildAsync(PreV9Config);

        var ratios = HealthyRatios();
        ratios.Roe = 1.0;

        var r = await Score(sut, ratios, BcraNoDebt(), MacroBenign());

        Assert.Equal("Critico", Entry(r, "roe").Status);
        Assert.Equal(55.00m, r.Score);
    }

    // ── 3. Leverage aligned with endeudamiento ────────────────────────────────────────────────────

    [Theory]
    [InlineData(2.29, 48.50,  "Saludable", false)]  // (2.29-1.00)/1.33*50 = 48.496… — below 50, so not even "Atencion"
    [InlineData(2.33, 50.00,  "Atencion",  false)]  // == warn, the endeudamiento-0.70 equivalent
    [InlineData(5.67, 100.00, "Critico",   true)]   // == danger, the endeudamiento-0.85 equivalent
    public async Task Leverage_IsScoredOnTheEndeudamientoEquivalentThresholds(
        double leverage, double expectedPartial, string expectedStatus, bool expectedCritical)
    {
        var sut = await BuildAsync();

        var ratios = HealthyRatios();
        ratios.Leverage = leverage;

        var r = await Score(sut, ratios, BcraNoDebt(), MacroBenign());

        Assert.Equal((decimal)expectedPartial, Entry(r, "leverage").Score);
        Assert.Equal(expectedStatus, Entry(r, "leverage").Status);
        Assert.Equal(expectedCritical, r.CriticalRatios.Contains("leverage"));
    }

    // The defaults ARE the endeudamiento anchors through L = E/(1−E), to 2dp. Pinned against the
    // formula so a later edit to one pair cannot silently re-open the inconsistency.
    [Fact]
    public void LeverageDefaults_AreTheEndeudamientoAnchorsRestated()
    {
        static decimal AsLeverage(string endeudKey)
        {
            decimal e = ScoringDefaults.Get(endeudKey);
            return Math.Round(e / (1m - e), 2);
        }

        Assert.Equal(AsLeverage("threshold_endeudamiento_ok"),     ScoringDefaults.Get("threshold_leverage_ok"));
        Assert.Equal(AsLeverage("threshold_endeudamiento_warn"),   ScoringDefaults.Get("threshold_leverage_warn"));
        Assert.Equal(AsLeverage("threshold_endeudamiento_danger"), ScoringDefaults.Get("threshold_leverage_danger"));
        Assert.Equal(100m, ScoringDefaults.Get("critical_floor_trigger_threshold"));
    }

    // ── 4. The floor configuration is recorded for the chat ───────────────────────────────────────

    [Fact]
    public async Task Calculate_RecordsTheTriggerAndEveryRatiosFloor()
    {
        var sut = await BuildAsync();

        var r = await Score(sut, HealthyRatios(), BcraNoDebt(), MacroBenign());

        Assert.Equal(100m, r.CriticalFloorTrigger);
        Assert.Equal(new Dictionary<string, decimal>
        {
            ["liquidez_corriente"]  = 60m,
            ["prueba_acida"]        = 50m,   // v11 default (was 60): the acid-test floor needs a weak current ratio
            ["endeudamiento"]       = 65m,
            ["leverage"]            = 65m,
            ["cobertura_intereses"] = 70m,
            ["roe"]                 = 55m,
            ["roa"]                 = 55m,
            ["margen_neto"]         = 55m,
        }, r.CriticalFloors);
    }

    // A floor configured past the scale is recorded as the floor the engine would really apply, the
    // same clamp AppliedFloor carries — the chat must never quote a floor the score could not take.
    [Fact]
    public async Task Calculate_RecordsTheClampedFloor_NotTheConfiguredOne()
    {
        var sut = await BuildAsync(("critical_floor_roe", 150m), ("critical_floor_trigger_threshold", 85m));

        var r = await Score(sut, HealthyRatios(), BcraNoDebt(), MacroBenign());

        Assert.Equal(100m, r.CriticalFloors["roe"]);
        Assert.Equal(85m,  r.CriticalFloorTrigger);
    }

    // ── 5. The synthetic cases in the PR's before/after table ─────────────────────────────────────
    //
    // Each row is scored twice, under the pre-v9 config and under the v9 defaults. BCRA situación 1
    // (score 0) and macro 30, as for the Comercio case. Only ROE 1 % changes: a thin but positive return is no
    // longer floored. A negative ROE and a real leverage of 6.0 stay floored — the protection the
    // floor exists for is untouched.

    public static TheoryData<string, decimal, decimal> SyntheticCases => new()
    {
        // case,           score before v9, score under v9
        { "healthy",        4.50m,  4.50m },
        { "roe_1pct",      55.00m,  9.00m },
        { "roe_negative",  55.00m, 55.00m },
        { "leverage_6",    65.00m, 65.00m },
    };

    [Theory]
    [MemberData(nameof(SyntheticCases))]
    public async Task SyntheticCases_BeforeAndAfter(string caso, decimal expectedBefore, decimal expectedAfter)
    {
        var ratios = HealthyRatios();
        switch (caso)
        {
            case "roe_1pct":     ratios.Roe = 1.0;  break;
            case "roe_negative": ratios.Roe = -5.0; break;
            case "leverage_6":
                ratios.Leverage  = 6.0;
                ratios.DebtRatio = 6.0 / 7.0;   // the same balance sheet: E = L/(1+L)
                break;
        }

        var before = await Score(await BuildAsync(PreV9Config), ratios, BcraDebtFound(1), MacroBenign());
        var after  = await Score(await BuildAsync(),            ratios, BcraDebtFound(1), MacroBenign());

        Assert.Equal(expectedBefore, before.Score);
        Assert.Equal(expectedAfter,  after.Score);
    }

    // ── 6. An incoherent merged triplet is never scored on ────────────────────────────────────────
    //
    // A PARTIAL override that the v8 default warn (1.50) left coherent is inverted by the v9 one (2.33):
    // a lone danger of 2.00 makes the merged leverage triplet 1.00 / 2.33 / 2.00. Scored as is, leverage
    // 2.00 hits "danger" → 100 → critical → floor 65. The engine refuses that triplet and scores the
    // ratio on its COMPLETE default triplet instead, so leverage 2.00 is (2.00-1.00)/(2.33-1.00)*50 =
    // 37.5939… (w .10), composite 37.5939*.10*.50 + 0 + 30*.15 = 6.3797 → 6.38, no floor.

    private static RatiosData HealthyWithLeverage(double leverage)
    {
        var ratios = HealthyRatios();
        ratios.Leverage = leverage;
        return ratios;
    }

    [Fact]
    public async Task IncoherentSectorTriplet_ADangerOnlySectorRow_IsScoredOnTheDefaultTriplet()
    {
        var sut = await BuildAsync(("threshold_leverage_danger__sector__Comercio", 2.00m));

        var r = await Score(sut, HealthyWithLeverage(2.00), BcraNoDebt(), MacroBenign(), entitySector: "Comercio");

        Assert.Equal(new[] { "leverage" }, r.ThresholdFallbacks);
        Assert.Equal(37.59m, Entry(r, "leverage").Score);
        Assert.Equal("Saludable", Entry(r, "leverage").Status);
        Assert.Empty(r.CriticalRatios);
        Assert.Equal(0m,    r.AppliedFloor);
        Assert.Equal(6.38m, r.Score);
    }

    // The sector row only applies to its own sector: a General entity of the same tenant merges no
    // sector rows, finds the default triplet coherent, and records no fallback.
    [Fact]
    public async Task IncoherentSectorTriplet_DoesNotAffectAnotherSector()
    {
        var sut = await BuildAsync(("threshold_leverage_danger__sector__Comercio", 2.00m));

        var r = await Score(sut, HealthyWithLeverage(2.00), BcraNoDebt(), MacroBenign());

        Assert.Empty(r.ThresholdFallbacks);
        Assert.Equal(37.59m, Entry(r, "leverage").Score);
    }

    [Fact]
    public async Task IncoherentBaseTriplet_ADangerOnlyBaseRow_IsScoredOnTheDefaultTriplet()
    {
        var sut = await BuildAsync(("threshold_leverage_danger", 2.00m));

        var r = await Score(sut, HealthyWithLeverage(2.00), BcraNoDebt(), MacroBenign());

        Assert.Equal(new[] { "leverage" }, r.ThresholdFallbacks);
        Assert.Equal(37.59m, Entry(r, "leverage").Score);
        Assert.Empty(r.CriticalRatios);
        Assert.Equal(6.38m, r.Score);
    }

    // ALL THREE values are replaced, never a mix. The stored ok 0.50 is coherent on its own; if it
    // survived next to the default warn and danger, leverage 1.00 would score (1.00-0.50)/(2.33-0.50)*50
    // = 13.66 instead of the default triplet's 0.
    [Fact]
    public async Task IncoherentTriplet_IsReplacedWhole_NeverMixed()
    {
        var sut = await BuildAsync(
            ("threshold_leverage_ok",     0.50m),
            ("threshold_leverage_warn",   3.00m),
            ("threshold_leverage_danger", 2.00m));

        var r = await Score(sut, HealthyWithLeverage(1.00), BcraNoDebt(), MacroBenign());

        Assert.Equal(new[] { "leverage" }, r.ThresholdFallbacks);
        Assert.Equal(0m, Entry(r, "leverage").Score);
    }

    // A coherent custom triplet — here the whole v8 leverage calibration — is scored exactly as stored.
    [Fact]
    public async Task CoherentCustomTriplet_IsNotTouched()
    {
        var sut = await BuildAsync(
            ("threshold_leverage_warn",   1.50m),
            ("threshold_leverage_danger", 2.50m));

        var r = await Score(sut, HealthyWithLeverage(2.20), BcraNoDebt(), MacroBenign());

        Assert.Empty(r.ThresholdFallbacks);
        Assert.Equal(85.00m, Entry(r, "leverage").Score);   // 50+(2.20-1.50)/(2.50-1.50)*50
    }

    // The shipped defaults are coherent for every ratio, so a tenant with no rows never falls back.
    [Fact]
    public void ShippedDefaults_AreCoherentForEveryRatio()
    {
        foreach (var ratio in ScoringDefaults.ThresholdRatios)
        {
            Assert.True(
                ScoringDefaults.IsThresholdTripletOrdered(
                    ratio,
                    ScoringDefaults.Get($"threshold_{ratio}_ok"),
                    ScoringDefaults.Get($"threshold_{ratio}_warn"),
                    ScoringDefaults.Get($"threshold_{ratio}_danger")),
                ratio);
        }
    }
}
