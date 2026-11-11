// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-05

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Lufe;
using Api.Web.FinanceIA.Servicios;
using Xunit;

using static Api.Web.FinanceIA.Tests.Servicios.ScoringTestHarness;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// §7.3a — a drop in the monthly sales LUFE reports penalises the FINAL score (scoring algorithm 6),
/// in its NOMINAL first phase.
///
/// <para>
/// Every case here is pinned by the same arithmetic. <c>BaselineRatios()</c> scores ratios at 21.00
/// with no partial anywhere near the (v9: 100) critical trigger, <c>BcraNoDebt()</c> pins the BCRA term at 0
/// without excluding it, and <c>MacroBenign()</c> pins macro at 30, so the composite is
///
///   21.00 × 0.50 + 0 × 0.35 + 30 × 0.15 = 15.00
///
/// with no floor binding. The final score is therefore exactly <c>15.00 + ventasAdj</c>, which makes
/// the penalty directly readable off <c>Score</c> as well as off <c>VentasAdj</c>.
/// </para>
/// <para>
/// Default parameters: plena 10 points, umbral_inicio -0.10, umbral_plena -0.35. A drop shallower
/// than 10 % sits in the dead-band and costs nothing; past it the ramp runs to 10 points at -35 %.
/// The two thresholds are still 0.25 apart, so the SLOPE is exactly the one §7.3a chose — the whole
/// ramp simply slid 0.10 further negative when the nominal-era calibration was revised for a signal
/// that §7.3b had started measuring in real terms.
/// </para>
/// </summary>
public sealed class ScoringServiceVentasTests
{
    private const decimal Plena = 10.00m;

    // ── 1. The penalty scales linearly with the size of the drop ────────────────────────────────
    //
    // Thirteen flat months with only the last one moved, so the interanual reading is exact.
    //
    //   variación   fracción = (−0.10 − v) / 0.25   adj = 10 × fracción   Score = 15.00 + adj
    //     −0.50                  1.00 (capped)                     10.00                 25.00
    //     −0.35                  1.00                              10.00                 25.00
    //     −0.30                  0.80                               8.00                 23.00
    //     −0.225                 0.50                               5.00                 20.00
    //     −0.20                  0.40                               4.00                 19.00
    //     −0.15                  0.20                               2.00                 17.00
    //     −0.10                  0.00 (at the start threshold)      0.00                 15.00
    //     −0.08                  0.00 (dead band)                   0.00                 15.00
    //      0.00                  0.00                               0.00                 15.00

    // Variations travel as doubles because [InlineData] cannot carry a decimal literal — the same
    // reason SettingsServiceScoringValidationTests passes its values as strings. The double→decimal
    // cast rounds to 15 significant digits, so every short literal below round-trips to exactly the
    // decimal it is written as and the conversion cannot move a boundary.
    [Theory]
    [InlineData(-0.50, 10.00, 25.00, "Low")]      // past umbral_plena → capped at the plena
    [InlineData(-0.35, 10.00, 25.00, "Low")]      // AT umbral_plena → full penalty
    [InlineData(-0.30,  8.00, 23.00, "Low")]      // deep in the ramp
    [InlineData(-0.225, 5.00, 20.00, "Low")]      // the exact midpoint → half the plena
    [InlineData(-0.20,  4.00, 19.00, "Low")]      // between → linear
    [InlineData(-0.15,  2.00, 17.00, "Low")]      // just past umbral_inicio → a small penalty
    [InlineData(-0.10,  0.00, 15.00, "Low")]      // AT umbral_inicio → nothing: the boundary is inclusive
    [InlineData(-0.08,  0.00, 15.00, "Low")]      // inside the dead band → nothing, but still REPORTED
    [InlineData( 0.00,  0.00, 15.00, "Low")]      // flat → nothing
    public async Task Calculate_VentasPenalty_ScalesWithTheSizeOfTheDrop(
        double variacion,
        double expectedAdj,
        double expectedScore,
        string expectedLevel)
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            ventas: VentasInteranual((decimal)variacion));

        Assert.Equal((decimal)variacion,   r.VentasVariacion);
        Assert.Equal("interanual",         r.VentasBase);
        Assert.Equal((decimal)expectedAdj, r.VentasAdj);
        Assert.Equal((decimal)expectedScore, r.Score);
        Assert.Equal(expectedLevel,        r.Level);

        // An adjustment on the composite, NOT a fourth component: the 0.50 / 0.35 / 0.15 split is
        // untouched and no renormalisation happened. This is the whole reason it lives here and not
        // inside a component — LUFE is optional per tenant.
        Assert.Equal(0.5000m, r.EffectiveWeightRatios);
        Assert.Equal(0.3500m, r.EffectiveWeightBcra);
        Assert.Equal(0.1500m, r.EffectiveWeightMacro);
        Assert.Equal(21.00m,  r.ScoreRatios);
        Assert.False(r.BcraExcluded);
    }

    // ── 2. Asymmetry: growth never moves the score ──────────────────────────────────────────────
    //
    // The signal only ever raises risk. A company that grew is not rewarded with a lower score, which
    // is the mirror of the cheques block's refusal to treat a clean record as a bonus. The variation
    // is still REPORTED, because a measured +40 % is a real reading, not a missing one.

    [Theory]
    [InlineData(0.40)]
    [InlineData(0.05)]
    [InlineData(2.00)]
    public async Task Calculate_WhenSalesGrew_NoPenaltyIsApplied(double variacion)
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            ventas: VentasInteranual((decimal)variacion));

        Assert.Equal(0m,     r.VentasAdj);
        Assert.Equal(15.00m, r.Score);

        Assert.Equal((decimal)variacion, r.VentasVariacion);   // measured, not missing
        Assert.Equal("interanual",       r.VentasBase);
    }

    // ── 3. The quarterly fallback ───────────────────────────────────────────────────────────────

    // Six months and no month twelve back, so the interanual pair cannot be formed at all. The last
    // three months sum to 240.000 against 300.000 for the three before them: v = −0.20 → adj 4.00.
    [Fact]
    public async Task Calculate_WhenTheYearEarlierMonthIsAbsent_FallsBackToTheQuarterlyBasis()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            ventas: Ventas(
                ("202508", 100_000m), ("202509", 100_000m), ("202510", 100_000m),
                ("202511",  80_000m), ("202512",  80_000m), ("202601",  80_000m)));

        Assert.Equal("trimestral", r.VentasBase);
        Assert.Equal(-0.20m,       r.VentasVariacion);
        Assert.Equal(4.00m,        r.VentasAdj);
        Assert.Equal(19.00m,       r.Score);
    }

    // The interanual pair wins whenever it CAN be formed, even with plenty of months for a quarterly
    // reading. Here the two bases disagree on purpose — interanual is flat, the recent quarter is
    // sharply down — and the reported basis proves which one was used.
    [Fact]
    public async Task Calculate_WhenBothBasesArePossible_TheInteranualOneIsUsed()
    {
        var sut = await BuildAsync();

        var serie = VentasInteranual(0m);              // 202501..202601, flat at 100.000
        serie[^2].Ventas = 10_000m;                    // collapse the middle of the recent quarter
        serie[^3].Ventas = 10_000m;

        var r = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), ventas: serie);

        Assert.Equal("interanual", r.VentasBase);
        Assert.Equal(0m,           r.VentasVariacion);   // flat year on year
        Assert.Equal(0m,           r.VentasAdj);
    }

    // ── 4. When no basis can be formed at all ───────────────────────────────────────────────────
    //
    // Not evaluated is distinct from evaluated-at-zero: the adjustment is 0 AND both reported fields
    // are null, so a consumer can tell "sales did not fall" from "we could not tell".

    [Fact]
    public async Task Calculate_WithNoSeriesAtAll_TheSignalIsNotEvaluated()
        => await AssertNotEvaluated(null);

    [Fact]
    public async Task Calculate_WithAnEmptySeries_TheSignalIsNotEvaluated()
        => await AssertNotEvaluated(new List<LufeVentaCompraMes>());

    [Fact]
    public async Task Calculate_WithASingleMonth_TheSignalIsNotEvaluated()
        => await AssertNotEvaluated(Ventas(("202601", 100_000m)));

    // Five months: no month twelve back, and one short of the six a quarterly reading needs.
    [Fact]
    public async Task Calculate_WithFiveMonths_TheSignalIsNotEvaluated()
        => await AssertNotEvaluated(Ventas(
            ("202509", 100m), ("202510", 100m), ("202511", 100m),
            ("202512", 100m), ("202601", 100m)));

    // Six entries, but only five carry a sales figure: a month LUFE reported with no sales
    // declaration is not a month, and must not be padded to zero to reach the quarter.
    [Fact]
    public async Task Calculate_WithSixMonthsOneOfThemUndeclared_TheSignalIsNotEvaluated()
        => await AssertNotEvaluated(Ventas(
            ("202508", 100m), ("202509", null), ("202510", 100m),
            ("202511", 100m), ("202512", 100m), ("202601", 100m)));

    // Malformed period keys never enter the comparison — a stored blob can carry anything.
    [Fact]
    public async Task Calculate_WithUnparseablePeriods_TheSignalIsNotEvaluated()
        => await AssertNotEvaluated(Ventas(("no-es-un-periodo", 100m), ("2026-01", 100m)));

    [Fact]
    public async Task Calculate_WithMonthsOutsideOneToTwelve_TheSignalIsNotEvaluated()
        => await AssertNotEvaluated(Ventas(("202613", 100m), ("202600", 100m)));

    private static async Task AssertNotEvaluated(List<LufeVentaCompraMes>? serie)
    {
        var sut = await BuildAsync();

        var r = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), ventas: serie);

        Assert.Equal(0m, r.VentasAdj);
        Assert.Null(r.VentasVariacion);
        Assert.Null(r.VentasBase);
        Assert.Equal(15.00m, r.Score);
    }

    // A zero comparison base is the divisor, and a fraction against no sales says nothing about a
    // trend — so the interanual pair is unusable and the series falls through. With only two months
    // there is no quarterly basis either, so nothing is evaluated. Critically, this is NOT scored as
    // an infinite rise or a divide-by-zero.
    [Fact]
    public async Task Calculate_WhenTheYearEarlierMonthIsZero_ThatPairIsNotUsed()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            ventas: Ventas(("202501", 0m), ("202601", 80_000m)));

        Assert.Equal(0m, r.VentasAdj);
        Assert.Null(r.VentasVariacion);
        Assert.Null(r.VentasBase);
    }

    // ── 5. Ragged series ────────────────────────────────────────────────────────────────────────

    // A month missing from the MIDDLE of the series must not shift the −12 match: the pair is found
    // by period key, so counting back twelve entries would have compared the wrong months. Here
    // 202509 is absent entirely and 202511 carries no sales figure, and the reading is still exact.
    [Fact]
    public async Task Calculate_WithAGappedSeries_TheInteranualMatchIsStillByPeriodKey()
    {
        var sut = await BuildAsync();

        var serie = VentasInteranual(-0.20m);                       // 202501..202601
        serie.RemoveAll(m => m.Periodo == "202509");                // a month LUFE never reported
        serie.Single(m => m.Periodo == "202511").Ventas = null;     // reported, but no sales declared

        var r = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), ventas: serie);

        Assert.Equal("interanual", r.VentasBase);
        Assert.Equal(-0.20m,       r.VentasVariacion);
        Assert.Equal(4.00m,        r.VentasAdj);
    }

    // The quarter is "the three most recent months CARRYING sales", not "the last three calendar
    // months". 202510 is missing and 202511 declares nothing, so the recent quarter is
    // 202509 + 202512 + 202601 and the previous one is 202506 + 202507 + 202508 — and a null is never
    // read as a zero sale, which would have manufactured a collapse out of a filing gap.
    [Fact]
    public async Task Calculate_WithAGappedSeries_TheQuartersSkipMonthsWithoutSales()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            ventas: Ventas(
                ("202506", 100_000m), ("202507", 100_000m), ("202508", 100_000m),
                ("202509", 100_000m), ("202511", null),     ("202512",  70_000m),
                ("202601",  70_000m)));

        Assert.Equal("trimestral", r.VentasBase);
        Assert.Equal(-0.20m,       r.VentasVariacion);   // 240.000 vs 300.000
        Assert.Equal(4.00m,        r.VentasAdj);
    }

    // ── 6. Tenant configuration ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Calculate_VentasPenalty_HonoursTheSeededParameters()
    {
        // A tenant that penalises harder (30 points) and from a shallower 5 % drop, reaching the full
        // penalty at 25 %: v = −0.15 sits halfway → 15.00 points. All three seeds differ from the
        // shipped defaults, so none of them is silently doing nothing: on the defaults this same
        // series would score 2.00.
        var sut = await BuildAsync(
            ("lufe_ventas_penalizacion_plena", 30m),
            ("lufe_ventas_umbral_inicio",     -0.05m),
            ("lufe_ventas_umbral_plena",      -0.25m));

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            ventas: VentasInteranual(-0.15m));

        Assert.Equal(15.00m, r.VentasAdj);
        Assert.Equal(30.00m, r.Score);
    }

    // A tenant can WIDEN the dead-band past the shipped one: with a start threshold of −20 %, a 15 %
    // drop is inside the band this tenant declared uninteresting and moves nothing — while still
    // being reported as measured. The seed has to sit off the default for this to prove anything:
    // on the shipped −10 % the same series would score 2.00 points, so deleting the seed fails it.
    [Fact]
    public async Task Calculate_WhenTheDropIsInsideTheDeadBand_NoPenaltyIsApplied()
    {
        var sut = await BuildAsync(("lufe_ventas_umbral_inicio", -0.20m));

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            ventas: VentasInteranual(-0.15m));

        Assert.Equal(0m,      r.VentasAdj);
        Assert.Equal(15.00m,  r.Score);
        Assert.Equal(-0.15m,  r.VentasVariacion);
    }

    // These thresholds are a PROVISIONAL calibration, chosen on economic-principle grounds rather
    // than fitted to observed outcomes — §7.3a's nominal numbers, revised once §7.3b started
    // measuring the variation in real terms and made them far too strict. They are pinned here so
    // the recalibration that is still owed (against which real sales drops actually preceded
    // default) arrives as a deliberate act with this assertion in the diff, not as a drift.
    [Fact]
    public void VentasPenalty_ShippedDefaults_AreTheProvisionalRealCalibration()
    {
        Assert.Equal(Plena,    ScoringDefaults.Get("lufe_ventas_penalizacion_plena"));
        Assert.Equal(-0.1000m, ScoringDefaults.Get("lufe_ventas_umbral_inicio"));
        Assert.Equal(-0.3500m, ScoringDefaults.Get("lufe_ventas_umbral_plena"));
        Assert.Equal(1.0000m,  ScoringDefaults.Get("lufe_ventas_enabled"));

        // The RAMP WIDTH is the invariant the recalibration preserved: it slid the whole ramp 0.10
        // more negative without touching its slope. An edit that changes the slope rather than the
        // centring is a different kind of decision, and this pins it as its own visible diff.
        Assert.Equal(
            0.2500m,
            ScoringDefaults.Get("lufe_ventas_umbral_inicio") -
            ScoringDefaults.Get("lufe_ventas_umbral_plena"));
    }

    // Off means never evaluated: adjustment zero AND both reported fields null, exactly like an
    // analysis with no series at all. This is the per-signal switch — scoring does not consult the
    // tenant profile, so this key is how a LUFE-enabled tenant turns just this signal off.
    [Fact]
    public async Task Calculate_WhenTheTenantDisablesTheSignal_NoPenaltyIsApplied()
    {
        var sut = await BuildAsync(("lufe_ventas_enabled", 0m));

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            ventas: VentasInteranual(-0.50m));

        Assert.Equal(0m, r.VentasAdj);
        Assert.Null(r.VentasVariacion);
        Assert.Null(r.VentasBase);
        Assert.Equal(15.00m, r.Score);
    }

    // Config rows are per-tenant: another tenant's disable must not reach this one.
    [Fact]
    public async Task Calculate_AnotherTenantsDisableRow_DoesNotReachThisTenant()
    {
        var sut = await BuildFromRowsAsync(Row(OtherTenant, "lufe_ventas_enabled", 0m));

        // Deliberately far past umbral_plena rather than sitting on it: this test is about the row
        // not crossing tenants, so it should not re-pin the ramp and break on every recalibration.
        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            ventas: VentasInteranual(-0.50m));

        Assert.Equal(Plena, r.VentasAdj);
    }

    // ── 7. Clamps ───────────────────────────────────────────────────────────────────────────────

    // A drop far past umbral_plena is capped at the full plena — the penalty is bounded by the
    // parameter, not by the size of the collapse.
    [Fact]
    public async Task Calculate_WhenTheDropExceedsTheFullThreshold_ThePenaltyIsCapped()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            ventas: VentasInteranual(-0.95m));

        Assert.Equal(Plena,  r.VentasAdj);
        Assert.Equal(25.00m, r.Score);
    }

    // VentasAdj is the REALISED movement after the 0-100 clamp, not the penalty the parameters
    // proposed. The PDF and the chat print this number, so reporting the proposed 100 here would
    // claim points the score never carried — the exact mis-statement the trend adjustment was once
    // fixed to stop making.
    //
    // BeyondDangerRatios + situación 6 + Macro(15.0) is the composite
    // ScoringServiceFloorAndLevelTests already pins at 90.25 (100 × 0.50 + 100 × 0.35 + 35 × 0.15),
    // with the 70 floor computed but not binding. A proposed 100-point penalty can therefore only
    // land 9.75 of itself before the score hits the ceiling.
    [Fact]
    public async Task Calculate_WhenThePenaltyWouldPushPastTheCeiling_OnlyTheRealisedPointsAreReported()
    {
        var sut = await BuildAsync(("lufe_ventas_penalizacion_plena", 100m));

        var r = await Score(
            sut, BeyondDangerRatios(), BcraDebtFound(6), Macro(15.0),
            ventas: VentasInteranual(-0.90m));

        Assert.Equal(90.25m,  r.ScoreBeforeFloor);   // the composite this started from
        Assert.Equal(100.00m, r.Score);              // clamped at the ceiling
        Assert.Equal(9.75m,   r.VentasAdj);          // proposed 100, realised 9.75
        Assert.Equal(-0.90m,  r.VentasVariacion);    // still measured in full
    }

    // A parameter outside the 0-100 point scale is not a penalty. SettingsService rejects it at the
    // door; the engine clamps it again, the same second layer the critical floor carries.
    //
    // The variation has to sit MID-RAMP for this to prove anything. At the full penalty the clamp is
    // invisible: 500 points and 100 points both bury the score in the 0-100 ceiling and report the
    // identical movement, so the assertion would hold with the clamp deleted. At −0.25 the fracción
    // is 0.60, so the clamped parameter yields 60.00 points and an unclamped one would yield 300.
    [Fact]
    public async Task Calculate_WithAnOutOfRangePlena_TheEngineClampsItAgain()
    {
        var sut = await BuildAsync(("lufe_ventas_penalizacion_plena", 500m));

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            ventas: VentasInteranual(-0.25m));

        Assert.Equal(60.00m, r.VentasAdj);   // 100 (clamped from 500) × 0.60
        Assert.Equal(75.00m, r.Score);       // 15.00 + 60.00 — nowhere near the ceiling
    }

    // Thresholds that MEET make the ramp's denominator zero. That arm collapses to a step at the
    // shared threshold instead of dividing by it: full plena at or below, nothing above.
    [Theory]
    [InlineData(-0.30, 10.00)]  // below the step → the full plena
    [InlineData(-0.20, 10.00)]  // exactly at it  → the full plena
    [InlineData(-0.10,  0.00)]  // above it       → nothing
    public async Task Calculate_WhenTheThresholdsMeet_TheRampBecomesAStep(
        double variacion, double expectedAdj)
    {
        var sut = await BuildAsync(
            ("lufe_ventas_umbral_inicio", -0.20m),
            ("lufe_ventas_umbral_plena",  -0.20m));

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            ventas: VentasInteranual((decimal)variacion));

        Assert.Equal((decimal)expectedAdj, r.VentasAdj);
    }

    // ── 8. Determinism ──────────────────────────────────────────────────────────────────────────

    // The engine reads no clock: the "most recent month" is the greatest period PRESENT, never the
    // month it happens to be scored in. The same series anchored a decade apart must score
    // identically — the property algorithm version 2 established for the BCRA trend window, and the
    // reason this signal could not simply take "the last 12 months from today".
    [Fact]
    public async Task Calculate_TheSameSeriesScoredInAnyEra_YieldsTheSamePenalty()
    {
        var sut = await BuildAsync();

        var ahora  = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            ventas: VentasInteranual(-0.20m, ultimoPeriodo: "202601"));
        var futuro = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            ventas: VentasInteranual(-0.20m, ultimoPeriodo: "203801"));
        var pasado = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            ventas: VentasInteranual(-0.20m, ultimoPeriodo: "201001"));

        Assert.Equal(ahora.VentasAdj,       futuro.VentasAdj);
        Assert.Equal(ahora.VentasVariacion, futuro.VentasVariacion);
        Assert.Equal(ahora.Score,           futuro.Score);

        Assert.Equal(ahora.VentasAdj,       pasado.VentasAdj);
        Assert.Equal(ahora.VentasVariacion, pasado.VentasVariacion);
        Assert.Equal(ahora.Score,           pasado.Score);
    }

    // Nothing re-sorts a persisted blob on read, so the engine must not depend on the order it was
    // written in. Selection is by period key, so a shuffled series scores identically.
    [Fact]
    public async Task Calculate_TheSameSeriesInAnyOrder_YieldsTheSamePenalty()
    {
        var sut = await BuildAsync();

        var ascendente = VentasInteranual(-0.20m);
        var descendente = Enumerable.Reverse(ascendente).ToList();

        var a = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), ventas: ascendente);
        var d = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), ventas: descendente);

        Assert.Equal("interanual", d.VentasBase);
        Assert.Equal(a.VentasVariacion, d.VentasVariacion);
        Assert.Equal(a.VentasAdj,       d.VentasAdj);
    }

    // ── 9. The signal never throws ──────────────────────────────────────────────────────────────
    //
    // CalculateAsync is contractually never-throws, and a malformed stored series must cost the
    // analysis its sales signal and nothing else — not fall into the catch-all, which would discard
    // the whole real score for a neutral 50.

    [Fact]
    public async Task Calculate_WithAMalformedSeries_ScoresNormallyWithNoPenalty()
    {
        var sut = await BuildAsync();

        var serie = new List<LufeVentaCompraMes>
        {
            new() { Periodo = null!,   Ventas = 100m },     // a blob can carry anything
            new() { Periodo = "",      Ventas = 100m },
            new() { Periodo = "202601" },                   // no sales figure at all
        };

        var r = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), ventas: serie);

        Assert.Equal(15.00m, r.Score);       // the real composite, not the neutral 50 fallback
        Assert.Equal("11",    r.ScoringAlgoVersion);   // v11: the acid-test floor needs a weak current ratio (v10: zero current liabilities at the best band + profitability floor needs an operating loss)
        Assert.Equal(0m,     r.VentasAdj);
        Assert.Null(r.VentasVariacion);
    }

    // ── 10. The algorithm version ───────────────────────────────────────────────────────────────

    // The sales-drop penalty changes the engine's arithmetic, so it bumps the algorithm version.
    // Analyses scored before it are NOT recomputed: the stamp is what keeps an old score identifiable
    // as one an earlier engine produced. Asserted against the literal AND the constant, as the
    // cheques and BCRA suites do — the literal makes a future bump deliberate.
    [Fact]
    public async Task Calculate_WithTheVentasPenalty_StampsAlgorithmVersionEight()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            ventas: VentasInteranual(-0.20m));

        Assert.Equal("11", r.ScoringAlgoVersion);   // v11: the acid-test floor needs a weak current ratio (v10: zero current liabilities at the best band + profitability floor needs an operating loss)
        Assert.Equal(ScoringService.ScoringAlgoVersion, r.ScoringAlgoVersion);
    }

    // An analysis with no LUFE data scores exactly as it did before this signal existed — the
    // property that lets the penalty ship without touching a single pre-existing fixture.
    [Fact]
    public async Task Calculate_WithNoSalesSeries_ScoresExactlyAsBefore()
    {
        var sut = await BuildAsync();

        var sin = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign());

        Assert.Equal(15.00m, sin.Score);
        Assert.Equal(0m,     sin.VentasAdj);
        Assert.Null(sin.VentasVariacion);
        Assert.Null(sin.VentasBase);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  §7.3b — the same drop, measured in REAL terms (scoring algorithm 7)
    // ════════════════════════════════════════════════════════════════════════════════════════════
    //
    // Every case above still passes MacroBenign(), which carries no CPI series, so it keeps measuring
    // nominally and keeps its original figures. The cases below hand the engine a series and pin what
    // deflating actually does.
    //
    // THE DIRECTION, because it is the whole point and it is easy to get backwards. With
    // real = (1 + nominal) / D − 1 and D the compounded price factor over the span, D > 1 under any
    // positive inflation, so real <= nominal ALWAYS. Deflating can only reveal a drop, never erase
    // one: a nominal RISE that failed to keep up with prices becomes the real drop it was. The one
    // way a nominal drop can turn into a real rise is cumulative DEFLATION over the span, which is
    // exactly what the negative-CPI case below builds.
    //
    // Every CPI percentage here is chosen so (1 + pct/100) is exact in decimal — 25 → 1.25,
    // 10 → 1.1, −20 → 0.8 — so the compounded factor is exact and the sales figures can be solved
    // backwards to make the asserted variation a round number. No figure below is approximate.

    // ── 11. The headline: a nominal RISE scored as a real DROP ──────────────────────────────────
    //
    // Sales up 56.25 % quarter on quarter, prices up 95.3125 % over the same span:
    //
    //   previo   = 3 × 100.000 = 300.000     reciente = 3 × 156.250 = 468.750
    //   nominal  = 168.750 / 300.000                              = +0.5625
    //   D        = 1.25³                                          =  1.953125
    //   real     = 1.5625 / 1.953125 − 1                          = −0.20      (exact)
    //   fracción = 0.10 / 0.25 = 0.40   →   adj = 4.00   →   Score = 15.00 + 4.00 = 19.00
    //
    // §7.3a saw growth and applied nothing. That 0 → 4 points, on one series, IS §7.3b.
    [Fact]
    public async Task Calculate_WhenNominalGrowthTrailsInflation_TheRealDropIsPenalised()
    {
        var sut = await BuildAsync();

        var ventas = Ventas(
            ("202508", 100_000m), ("202509", 100_000m), ("202510", 100_000m),
            ("202511", 156_250m), ("202512", 156_250m), ("202601", 156_250m));

        // The span is periodos[^4] → periodos[^1], exclusive of the first: 202511, 202512, 202601.
        var ipc = IpcPlano(25.0, ultimoPeriodo: "202601", meses: 3);

        var real = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroConIpc(ipc), ventas: ventas);

        Assert.Equal("trimestral", real.VentasBase);
        Assert.Equal(-0.2000m,     real.VentasVariacion);
        Assert.True(real.VentasVariacionEsReal);
        Assert.Equal(4.00m,        real.VentasAdj);
        Assert.Equal(19.00m,       real.Score);

        // The A/B half: the same series with no CPI is the §7.3a reading — growth, no penalty.
        var nominal = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), ventas: ventas);

        Assert.Equal(0.5625m, nominal.VentasVariacion);
        Assert.False(nominal.VentasVariacionEsReal);
        Assert.Equal(0m,      nominal.VentasAdj);
        Assert.Equal(15.00m,  nominal.Score);
    }

    // Interanual, solved to the same exact −0.20 so it is directly comparable with the ramp table
    // at the top of this file, where −0.20 nominal also scores 4.00.
    //
    //   D        = 1.1¹²                              = 3,138428376721
    //   sales    = 100.000 → 0,8 × D × 100.000        = 251.074,27013768
    //   nominal  = +1,5107427013768   (a 151 % rise)
    //   real     = 2,5107427013768 / 3,138428376721 − 1 = −0.20   (exact)
    [Fact]
    public async Task Calculate_Interanual_DeflatesAcrossTheTwelveMonthsSeparatingThePair()
    {
        var sut = await BuildAsync();

        var ventas = Ventas(("202501", 100_000m), ("202601", 251_074.27013768m));

        // Twelve months ending at the recent one: 202502 … 202601. The comparison month itself is
        // NOT in the span — its own inflation belongs to the period before the one being measured.
        var ipc = IpcPlano(10.0, ultimoPeriodo: "202601", meses: 12);

        var real = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroConIpc(ipc), ventas: ventas);

        Assert.Equal("interanual", real.VentasBase);
        Assert.Equal(-0.2000m,     real.VentasVariacion);
        Assert.True(real.VentasVariacionEsReal);
        Assert.Equal(4.00m,        real.VentasAdj);
        Assert.Equal(19.00m,       real.Score);

        var nominal = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), ventas: ventas);

        Assert.Equal(1.5107m, nominal.VentasVariacion);   // rounded to 4dp on the way out
        Assert.False(nominal.VentasVariacionEsReal);
        Assert.Equal(0m,      nominal.VentasAdj);
    }

    // Flat nominal sales are a real collapse once prices move: 1 / 1.1¹² − 1 = −0,6813691823…,
    // well past umbral_plena, so the penalty caps at the full 10 points. Under §7.3a this exact
    // series scored 0.00 (it is the last row of the table at the top of this file).
    [Fact]
    public async Task Calculate_WhenNominalSalesAreFlat_TheRealVariationIsADrop()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(),
            MacroConIpc(IpcPlano(10.0, ultimoPeriodo: "202601", meses: 12)),
            ventas: Ventas(("202501", 100_000m), ("202601", 100_000m)));

        Assert.Equal(-0.6814m, r.VentasVariacion);
        Assert.True(r.VentasVariacionEsReal);
        Assert.Equal(Plena,  r.VentasAdj);
        Assert.Equal(25.00m, r.Score);
    }

    // The quarterly deflator spans the most recent month of the PREVIOUS quarter to the most recent
    // month of the CURRENT one — not "three months". On a ragged series those are five months apart,
    // and the reading is still exact:
    //
    //   meses con ventas: 202506, 202507, 202508 | 202511, 202512, 202601
    //   periodos[^4] = 202508, periodos[^1] = 202601  →  span 202509 … 202601 (5 months)
    //   D        = 1.1⁵                                    = 1,61051
    //   previo   = 300.000    reciente = 3 × 128.840,80    = 386.522,40
    //   nominal  = +0,288408
    //   real     = 1,288408 / 1,61051 − 1                  = −0.20   (exact)
    [Fact]
    public async Task Calculate_Trimestral_DeflatesFromTheMostRecentMonthOfEachQuarter()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(),
            MacroConIpc(IpcPlano(10.0, ultimoPeriodo: "202601", meses: 5)),
            ventas: Ventas(
                ("202506", 100_000m),    ("202507", 100_000m),    ("202508", 100_000m),
                ("202511", 128_840.80m), ("202512", 128_840.80m), ("202601", 128_840.80m)));

        Assert.Equal("trimestral", r.VentasBase);
        Assert.Equal(-0.2000m,     r.VentasVariacion);
        Assert.True(r.VentasVariacionEsReal);
        Assert.Equal(4.00m,        r.VentasAdj);
    }

    // ── 12. Cumulative deflation is the ONLY way a drop becomes a rise ──────────────────────────
    //
    // The same six months the quarterly-fallback fixture above uses — 100.000 falling to 80.000,
    // −20 % nominal, 4.00 points on the shipped ramp — with prices falling 20 % a month:
    //
    //   D    = 0.8³ = 0,512      real = 0,80 / 0,512 − 1 = +0,5625   (exact)
    //
    // Sales fell in pesos and rose in goods, so the penalty correctly disappears. This is the
    // "nominal drop that is a real rise" case, and it is unreachable with positive inflation.
    [Fact]
    public async Task Calculate_UnderCumulativeDeflation_ANominalDropCanBeARealRise()
    {
        var sut = await BuildAsync();

        var ventas = Ventas(
            ("202508", 100_000m), ("202509", 100_000m), ("202510", 100_000m),
            ("202511",  80_000m), ("202512",  80_000m), ("202601",  80_000m));

        var real = await Score(
            sut, BaselineRatios(), BcraNoDebt(),
            MacroConIpc(IpcPlano(-20.0, ultimoPeriodo: "202601", meses: 3)),
            ventas: ventas);

        Assert.Equal(0.5625m, real.VentasVariacion);
        Assert.True(real.VentasVariacionEsReal);
        Assert.Equal(0m,      real.VentasAdj);
        Assert.Equal(15.00m,  real.Score);

        // Without the series it is the nominal reading: a 20 % drop, 4.00 points.
        var nominal = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), ventas: ventas);

        Assert.Equal(-0.20m, nominal.VentasVariacion);
        Assert.Equal(4.00m,  nominal.VentasAdj);
    }

    // ── 13. The nominal fallback is reported, never silent ──────────────────────────────────────

    // No macro payload at all. The signal survives on the nominal figure — a real sales drop is
    // information, and losing it because a macro auxiliary was missing would be worse than measuring
    // it in pesos. Note the composite baseline moves to 18.00 here: a missing inflation FIGURE scores
    // macro at the neutral 50 rather than the 30 base (algorithm version 3), which is unrelated to
    // this penalty and is why the score is 18.00 + 4.00 rather than 15.00 + 4.00.
    [Fact]
    public async Task Calculate_WithNoMacroPayloadAtAll_MeasuresNominallyAndSaysSo()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), macro: null,
            ventas: VentasInteranual(-0.20m));

        Assert.Equal(-0.20m,       r.VentasVariacion);
        Assert.Equal("interanual", r.VentasBase);
        Assert.False(r.VentasVariacionEsReal);
        Assert.Equal(4.00m,        r.VentasAdj);
        Assert.Equal(22.00m,       r.Score);
    }

    // A CPI series that does not span the chosen basis falls back to nominal AND KEEPS THE BASIS.
    // The series here covers 202511/202512/202601 — exactly the three months a TRIMESTRAL span over
    // this same data would need — so if the engine ever re-selected the basis when deflation failed,
    // this test would see "trimestral" with EsReal true. Which months are compared must be settled
    // before any CPI is consulted; otherwise the availability of an auxiliary series decides which
    // question the score answers.
    [Fact]
    public async Task Calculate_WhenTheIpcDoesNotSpanTheBasis_FallsBackToNominalWithoutReselectingIt()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(),
            MacroConIpc(IpcPlano(10.0, ultimoPeriodo: "202601", meses: 3)),
            ventas: VentasInteranual(-0.20m));

        Assert.Equal("interanual", r.VentasBase);
        Assert.Equal(-0.20m,       r.VentasVariacion);
        Assert.False(r.VentasVariacionEsReal);
        Assert.Equal(4.00m,        r.VentasAdj);
    }

    // Twelve months of CPI, but ending one month too early: the span needs 202502…202601 and this
    // stops at 202512. A span is covered or it is not — there is no partial deflation.
    [Fact]
    public async Task Calculate_WhenTheIpcStopsOneMonthShort_MeasuresNominally()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(),
            MacroConIpc(IpcPlano(10.0, ultimoPeriodo: "202512", meses: 12)),
            ventas: VentasInteranual(-0.20m));

        Assert.Equal(-0.20m, r.VentasVariacion);
        Assert.False(r.VentasVariacionEsReal);
        Assert.Equal(4.00m,  r.VentasAdj);
    }

    // A hole INSIDE the span. A zero is read as "this month is missing", not as "prices did not
    // move": BcraMonetariasDetalleNode.Valor is a non-nullable double, so a wire point that arrived
    // without its valor deserialises to 0.0, and compounding that as 1.0 would turn absent data into
    // a confidently wrong real figure. Nominal, reported, is the safe side of that trade.
    [Fact]
    public async Task Calculate_WhenAMonthInsideTheSpanIsZero_MeasuresNominally()
    {
        var sut = await BuildAsync();

        var ipc = IpcPlano(10.0, ultimoPeriodo: "202601", meses: 12);
        ipc["202508"] = 0.0;

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroConIpc(ipc),
            ventas: Ventas(("202501", 100_000m), ("202601", 80_000m)));

        Assert.Equal(-0.20m, r.VentasVariacion);
        Assert.False(r.VentasVariacionEsReal);
        Assert.Equal(4.00m,  r.VentasAdj);
    }

    // An empty series is the same as no series — one shape for "nothing to deflate by".
    [Fact]
    public async Task Calculate_WithAnEmptyIpcSeries_MeasuresNominally()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroConIpc(Ipc()),
            ventas: VentasInteranual(-0.20m));

        Assert.False(r.VentasVariacionEsReal);
        Assert.Equal(4.00m, r.VentasAdj);
    }

    // Not evaluated at all keeps the flag null, alongside the variation and the basis — the same
    // convention chequesProporcionRecuperada follows. A false here would claim the engine looked and
    // found no inflation data, which is a different statement.
    [Fact]
    public async Task Calculate_WhenTheSignalIsNotEvaluated_TheRealFlagIsNullNotFalse()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(),
            MacroConIpc(IpcPlano(10.0, ultimoPeriodo: "202601", meses: 12)));

        Assert.Null(r.VentasVariacion);
        Assert.Null(r.VentasBase);
        Assert.Null(r.VentasVariacionEsReal);
        Assert.Equal(0m, r.VentasAdj);
    }

    // ── 14. Determinism, with the CPI series in the frozen payload ──────────────────────────────
    //
    // The deflator comes off the macro snapshot the analysis froze, not off a live fetch, so the
    // same payload scores identically whenever it is re-scored — the property algorithm version 2
    // established, now extended to cover the new input.
    [Fact]
    public async Task Calculate_TheSamePayloadWithIpcScoredInAnyEra_YieldsTheSameRealPenalty()
    {
        var sut = await BuildAsync();

        async Task<ScoringResult> En(string ultimo) => await Score(
            sut, BaselineRatios(), BcraNoDebt(),
            MacroConIpc(IpcPlano(10.0, ultimoPeriodo: ultimo, meses: 12)),
            ventas: VentasInteranual(0m, ultimoPeriodo: ultimo));

        var ahora  = await En("202601");
        var futuro = await En("203801");
        var pasado = await En("201001");

        Assert.True(ahora.VentasVariacionEsReal);
        Assert.Equal(-0.6814m, ahora.VentasVariacion);

        Assert.Equal(ahora.VentasVariacion,       futuro.VentasVariacion);
        Assert.Equal(ahora.VentasAdj,             futuro.VentasAdj);
        Assert.Equal(ahora.VentasVariacionEsReal, futuro.VentasVariacionEsReal);
        Assert.Equal(ahora.Score,                 futuro.Score);

        Assert.Equal(ahora.VentasVariacion,       pasado.VentasVariacion);
        Assert.Equal(ahora.VentasAdj,             pasado.VentasAdj);
        Assert.Equal(ahora.VentasVariacionEsReal, pasado.VentasVariacionEsReal);
        Assert.Equal(ahora.Score,                 pasado.Score);
    }

    // ── Three-month interanual basis (algorithm version 9) ──────────────────────────────────────
    //
    // The preferred basis sums the three calendar months ending at the most recent one against the
    // same three a year earlier, all six required. The fixtures below share one shape: the recent
    // quarter 202511..202601 flat at 100.000, and a base quarter 202411..202501 carrying ONE lumpy
    // month — 202501 at 150.000, the single-month basis's own comparison month. BaselineRatios +
    // BcraNoDebt + MacroBenign keep the composite at exactly 15.00; the ramp is the shipped one
    // (inicio −0.10, plena −0.35, 10 points).
    //
    //   interanual_3m : (300.000 − 350.000) / 350.000 = −0.142857…  → 10·(0.042857…/0.25) = 1.71
    //   interanual    : (100.000 − 150.000) / 150.000 = −0.333…     → 10·(0.2333…/0.25)   = 9.33
    //
    // One month in the base moves the single-month reading five times as far as the quarter's.

    private static List<LufeVentaCompraMes> SerieConMesAtipico(
        bool conHueco = false) => Ventas(
            new (string, decimal?)[]
            {
                ("202411", 100_000m),
                ("202412", conHueco ? null : 100_000m),   // null = no declaration, not zero sales
                ("202501", 150_000m),
                ("202511", 100_000m), ("202512", 100_000m), ("202601", 100_000m),
            });

    [Fact]
    public async Task Calculate_WithAllSixMonths_UsesTheThreeMonthBasis_AndDilutesALumpyMonth()
    {
        var sut = await BuildAsync();

        var r = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), ventas: SerieConMesAtipico());

        Assert.Equal("interanual_3m", r.VentasBase);
        Assert.Equal(-0.1429m,        r.VentasVariacion);
        Assert.False(r.VentasVariacionEsReal);            // MacroBenign carries no CPI series
        Assert.Equal(1.71m,           r.VentasAdj);
        Assert.Equal(16.71m,          r.Score);
    }

    // The same data with ONE of the six months missing: the three-month basis cannot form, and the
    // engine falls back to the single-month basis exactly as version 8 computed it — which reads the
    // lumpy month on its own.
    [Fact]
    public async Task Calculate_WithAGapAmongTheSixMonths_FallsBackToTheSingleMonthBasis()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), ventas: SerieConMesAtipico(conHueco: true));

        Assert.Equal("interanual", r.VentasBase);
        Assert.Equal(-0.3333m,     r.VentasVariacion);
        Assert.Equal(9.33m,        r.VentasAdj);
        Assert.Equal(24.33m,       r.Score);
    }

    // A gap in the RECENT quarter falls back the same way: the months are calendar months ending at
    // the latest one, never "the three most recent present" — that looser reading is the trimestral
    // fallback's, and borrowing it here would compare non-matching months year on year.
    [Fact]
    public async Task Calculate_WithAGapInTheRecentQuarter_FallsBackToTheSingleMonthBasis()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            ventas: Ventas(
                ("202411", 100_000m), ("202412", 100_000m), ("202501", 150_000m),
                ("202510", 100_000m), ("202511", 100_000m), ("202601", 100_000m)));

        Assert.Equal("interanual", r.VentasBase);
        Assert.Equal(-0.3333m,     r.VentasVariacion);
    }

    // The FALLBACK: a CPI series of exactly twelve readings ending at the last month covers the endpoint
    // span (202502..202601) but not the two older pairs, which need 202412 and 202501 too. So the basis
    // is deflated with ONE twelve-month factor between the last month and the same month a year earlier
    // — exactly as the single-month basis is — and is still reported as real. (Per-pair deflation, the
    // preferred path, is pinned below on a disinflation series, where the two readings differ.)
    [Fact]
    public async Task Calculate_TheThreeMonthBasis_WhenOnlyTheEndpointSpanIsCovered_UsesOneTwelveMonthFactor()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroConIpc(IpcPlano(10.0, "202601", 12)),
            ventas: SerieConMesAtipico());

        decimal nominal  = (300_000m - 350_000m) / 350_000m;
        decimal esperado = Math.Round(((1m + nominal) / 3.138428376721m) - 1m, 4);   // 1.1^12

        Assert.Equal("interanual_3m", r.VentasBase);
        Assert.True(r.VentasVariacionEsReal);
        Assert.Equal(esperado, r.VentasVariacion);
    }

    // ── Per-pair deflation across a disinflation ────────────────────────────────────────────────
    //
    // Monthly CPI (%), Dec-2023 → Jan-2025: the fall from 25.5 % to about 2 % a month. The window ends at
    // 202501, so the three pairs are 202501/202401, 202412/202312 and 202411/202311, and each is
    // separated by a different twelve readings:
    //   D(202401→202501) = Π 202402..202501 ≈ 1.8447
    //   D(202312→202412) = Π 202401..202412 ≈ 2.1768
    //   D(202311→202411) = Π 202312..202411 ≈ 2.6601
    // Base quarter flat at 100.000, recent quarter flat at 170.000 (+70 % nominal):
    //   per pair  : (170.000·(1/D₀ + 1/D₁ + 1/D₂) − 300.000) / 300.000 ≈ −0.2195 → penalty ≈ 4.78
    //   one factor: (510.000 / 300.000) / D₀ − 1                          ≈ −0.0785 → dead-band, 0
    // One factor restates the two older pairs at the latest pair's inflation — roughly 20 % and 45 % too
    // little — and hides a real fall of about 22 % inside the dead-band.

    private static Dictionary<string, double> IpcDesinflacion() => Ipc(
        ("202312", 25.5), ("202401", 20.6), ("202402", 13.2), ("202403", 11.0), ("202404", 8.8),
        ("202405", 4.2),  ("202406", 4.6),  ("202407", 4.0),  ("202408", 4.2),  ("202409", 3.5),
        ("202410", 2.7),  ("202411", 2.4),  ("202412", 2.7),  ("202501", 2.2));

    private static List<LufeVentaCompraMes> SerieDesinflacion() => Ventas(
        ("202311", 100_000m), ("202312", 100_000m), ("202401", 100_000m),
        ("202411", 170_000m), ("202412", 170_000m), ("202501", 170_000m));

    /// <summary>Π (1 + iₘ/100) over <paramref name="meses"/>, with the engine's own double → decimal steps.</summary>
    private static decimal Factor(Dictionary<string, double> ipc, params string[] meses)
    {
        decimal factor = 1m;
        foreach (var mes in meses)
            factor *= 1m + ((decimal)ipc[mes] / 100m);
        return factor;
    }

    private static readonly string[] Span202401a202501 =
        ["202402", "202403", "202404", "202405", "202406", "202407", "202408", "202409", "202410", "202411", "202412", "202501"];

    [Fact]
    public async Task Calculate_TheThreeMonthBasis_DeflatesEachPairWithItsOwnFactor()
    {
        var ipc = IpcDesinflacion();
        var sut = await BuildAsync();

        var r = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroConIpc(ipc), ventas: SerieDesinflacion());

        decimal d0 = Factor(ipc, Span202401a202501);
        decimal d1 = Factor(ipc, "202401", "202402", "202403", "202404", "202405", "202406", "202407", "202408", "202409", "202410", "202411", "202412");
        decimal d2 = Factor(ipc, "202312", "202401", "202402", "202403", "202404", "202405", "202406", "202407", "202408", "202409", "202410", "202411");

        decimal porPares = ((170_000m / d0) + (170_000m / d1) + (170_000m / d2) - 300_000m) / 300_000m;
        decimal unFactor = ((510_000m / 300_000m) / d0) - 1m;
        decimal ajuste   = Math.Round(10m * (-0.10m - porPares) / 0.25m, 2);

        Assert.Equal("interanual_3m", r.VentasBase);
        Assert.True(r.VentasVariacionEsReal);
        Assert.Equal(Math.Round(porPares, 4), r.VentasVariacion);
        Assert.Equal(-0.2195m, r.VentasVariacion);

        // The difference the per-pair reading makes: 14 points of real variation, and a penalty where one
        // factor would have found none.
        Assert.True(Math.Round(unFactor, 4) - r.VentasVariacion > 0.14m);
        Assert.True(unFactor > -0.10m, "one factor puts this in the dead-band");
        Assert.Equal(ajuste, r.VentasAdj);
        Assert.Equal(4.78m,  r.VentasAdj);
        Assert.Equal(15.00m + ajuste, r.Score);
    }

    // The same data with the two readings the OLDER pairs need removed (202312, 202401): the per-pair
    // reading cannot be formed, and the engine falls back to one factor over the endpoint span, which
    // is still covered — real, but the approximate reading, and no penalty.
    [Fact]
    public async Task Calculate_TheThreeMonthBasis_WhenThePairsAreNotAllCovered_FallsBackToTheEndpointFactor()
    {
        var ipc = IpcDesinflacion();
        ipc.Remove("202312");
        ipc.Remove("202401");
        var sut = await BuildAsync();

        var r = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroConIpc(ipc), ventas: SerieDesinflacion());

        decimal unFactor = ((510_000m / 300_000m) / Factor(ipc, Span202401a202501)) - 1m;

        Assert.Equal("interanual_3m", r.VentasBase);
        Assert.True(r.VentasVariacionEsReal);
        Assert.Equal(Math.Round(unFactor, 4), r.VentasVariacion);
        Assert.Equal(-0.0785m, r.VentasVariacion);
        Assert.Equal(0m,       r.VentasAdj);
    }

    // CPI that cannot cover the span sends the reading to nominal — it never re-selects the basis.
    [Fact]
    public async Task Calculate_TheThreeMonthBasis_WithShortCpi_StaysOnItsBasis_AndReportsNominal()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroConIpc(IpcPlano(10.0, "202601", 6)),
            ventas: SerieConMesAtipico());

        Assert.Equal("interanual_3m", r.VentasBase);
        Assert.False(r.VentasVariacionEsReal);
        Assert.Equal(-0.1429m, r.VentasVariacion);
    }

    // A base quarter summing to zero is no basis — it is the divisor. Every fallback is also
    // unformable here (the year-earlier month is zero, and the previous "quarter" sums to zero), so
    // the signal is not evaluated at all rather than reading zero base sales as infinite growth.
    [Fact]
    public async Task Calculate_WithAZeroBaseQuarter_FormsNoBasis()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            ventas: Ventas(
                ("202411", 0m), ("202412", 0m), ("202501", 0m),
                ("202511", 100_000m), ("202512", 100_000m), ("202601", 100_000m)));

        Assert.Null(r.VentasBase);
        Assert.Null(r.VentasVariacion);
        Assert.Equal(0m,     r.VentasAdj);
        Assert.Equal(15.00m, r.Score);
    }
}
