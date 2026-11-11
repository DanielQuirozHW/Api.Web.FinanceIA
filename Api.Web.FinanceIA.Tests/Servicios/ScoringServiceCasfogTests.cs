// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-05

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Xunit;

using static Api.Web.FinanceIA.Tests.Servicios.ScoringTestHarness;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// §7.4 — the guarantee arrears CASFOG reports penalise the FINAL score (scoring algorithm 8).
///
/// <para>
/// Every case here is pinned by the same arithmetic the §7.1 and §7.3 suites use.
/// <c>BaselineRatios()</c> scores ratios at 21.00 with no partial anywhere near the (v9: 100) critical
/// trigger, <c>BcraNoDebt()</c> pins the BCRA term at 0 without excluding it, and
/// <c>MacroBenign()</c> pins macro at 30, so the composite is
///
///   21.00 × 0.50 + 0 × 0.35 + 30 × 0.15 = 15.00
///
/// with no floor binding and no sales series, so the §7.3 penalty stays dormant. The final score is
/// therefore exactly <c>15.00 + casfogMoraAdj</c>, which makes the penalty directly readable off
/// <c>Score</c> as well as off <c>CasfogMoraAdj</c>.
/// </para>
/// <para>
/// Default parameters: plena 12 points, umbral_inicio 0.05, umbral_plena 0.40. A portfolio with under
/// 5 % of its guaranteed balance in arrears sits in the dead-band and costs nothing; past it the ramp
/// runs to 12 points at 40 %. The thresholds are 0.35 apart, and every proportion exercised below was
/// chosen so that <c>(p − 0.05) / 0.35</c> lands on an exact decimal — no case depends on rounding.
/// </para>
/// </summary>
public sealed class ScoringServiceCasfogTests
{
    private const decimal Plena = 12.00m;

    // ── 1. The penalty scales linearly with the share of the balance in arrears ─────────────────
    //
    // Two SGR rows, one of them in arrears, sized so the share is exact.
    //
    //   proporción   fracción = (p − 0.05) / 0.35   adj = 12 × fracción   Score = 15.00 + adj
    //      0.50               1.00 (capped)                        12.00                 27.00
    //      0.40               1.00                                 12.00                 27.00
    //      0.33               0.80                                  9.60                 24.60
    //      0.225              0.50                                  6.00                 21.00
    //      0.19               0.40                                  4.80                 19.80
    //      0.12               0.20                                  2.40                 17.40
    //      0.05               0.00 (at the start threshold)         0.00                 15.00
    //      0.03               0.00 (dead band)                      0.00                 15.00
    //      0.00               0.00                                  0.00                 15.00

    // Proportions travel as doubles because [InlineData] cannot carry a decimal literal — the same
    // reason the ventas suite does. The double→decimal cast rounds to 15 significant digits, so every
    // short literal below round-trips to exactly the decimal it is written as and the conversion
    // cannot move a boundary.
    [Theory]
    [InlineData(0.50,  12.00, 27.00, "Low")]      // past umbral_plena → capped at the plena
    [InlineData(0.40,  12.00, 27.00, "Low")]      // AT umbral_plena → full penalty
    [InlineData(0.33,   9.60, 24.60, "Low")]      // deep in the ramp
    [InlineData(0.225,  6.00, 21.00, "Low")]      // the exact midpoint → half the plena
    [InlineData(0.19,   4.80, 19.80, "Low")]      // between → linear
    [InlineData(0.12,   2.40, 17.40, "Low")]      // just past umbral_inicio → a small penalty
    [InlineData(0.05,   0.00, 15.00, "Low")]      // AT umbral_inicio → nothing: the boundary is inclusive
    [InlineData(0.03,   0.00, 15.00, "Low")]      // inside the dead band → nothing, but still REPORTED
    [InlineData(0.00,   0.00, 15.00, "Low")]      // nothing in arrears → nothing
    public async Task Calculate_CasfogMoraPenalty_ScalesWithTheShareOfBalanceInArrears(
        double proporcion,
        double expectedAdj,
        double expectedScore,
        string expectedLevel)
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            casfog: GarantiasProporcion((decimal)proporcion));

        Assert.Equal((decimal)proporcion,    r.CasfogProporcionMora);
        Assert.Equal((decimal)expectedAdj,   r.CasfogMoraAdj);
        Assert.Equal((decimal)expectedScore, r.Score);
        Assert.Equal(expectedLevel,          r.Level);

        // An adjustment on the composite, NOT a fourth component: the 0.50 / 0.35 / 0.15 split is
        // untouched and no renormalisation happened. This is the whole reason it lives here and not
        // inside a component — CASFOG is optional per tenant, and switched OFF by the non-SGR preset.
        Assert.Equal(0.5000m, r.EffectiveWeightRatios);
        Assert.Equal(0.3500m, r.EffectiveWeightBcra);
        Assert.Equal(0.1500m, r.EffectiveWeightMacro);
        Assert.Equal(21.00m,  r.ScoreRatios);
        Assert.False(r.BcraExcluded);
    }

    // ── 2. Exposure weighting — the whole point of a share over a count ──────────────────────────

    // The case that justifies aggregating by BALANCE rather than by row. Both portfolios have exactly
    // ONE SGR reporting arrears out of two rows, and both total 1,000,000 — a count-based signal would
    // score them identically. They are not the same risk: in the first the arrears sit in the SGR
    // holding 40 % of the guaranteed balance, in the second in the one holding 12 %.
    [Fact]
    public async Task Calculate_SameArrearsCountButDifferentExposure_ProducesDifferentPenalties()
    {
        var sut = await BuildAsync();

        // 400k in arrears + 600k clean → 0.40 of the balance, one row in arrears.
        var grande = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            casfog: Garantias((400_000m, 1), (600_000m, 0)));

        // 120k in arrears + 880k clean → 0.12 of the balance, also one row in arrears.
        var chico = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            casfog: Garantias((120_000m, 1), (880_000m, 0)));

        Assert.Equal(0.4000m, grande.CasfogProporcionMora);
        Assert.Equal(Plena,   grande.CasfogMoraAdj);
        Assert.Equal(27.00m,  grande.Score);

        Assert.Equal(0.1200m, chico.CasfogProporcionMora);
        Assert.Equal(2.40m,   chico.CasfogMoraAdj);
        Assert.Equal(17.40m,  chico.Score);

        Assert.NotEqual(grande.CasfogMoraAdj, chico.CasfogMoraAdj);
    }

    // A single row in arrears carrying the whole balance is total exposure, regardless of how many
    // clean rows sit beside it carrying nothing measurable.
    [Fact]
    public async Task Calculate_WhenEveryMeasurableBalanceIsInArrears_AppliesTheFullPlena()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            casfog: Garantias((250_000m, 3), (750_000m, 1)));

        Assert.Equal(1.0000m, r.CasfogProporcionMora);
        Assert.Equal(Plena,   r.CasfogMoraAdj);
        Assert.Equal(27.00m,  r.Score);
    }

    // The denominator is the SUM OF THE ROWS, never Garantias.SaldoBrutoGarantiasVigentes. Only the
    // rows are handed to the engine, so an aggregate that disagrees with them cannot reach it — this
    // pins that the numbers used come from the same place.
    [Fact]
    public async Task Calculate_UsesTheSummedRowBalances_NotAnyAggregateMetric()
    {
        var sut = await BuildAsync();

        // Rows sum to 500,000, of which 100,000 is in arrears → exactly 0.20, whatever any aggregate
        // elsewhere in the CASFOG payload might claim the vigente balance to be.
        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            casfog: Garantias((100_000m, 4), (400_000m, 0)));

        // (0.20 − 0.05) / 0.35 = 3/7, × 12 = 5.142857…, and the engine rounds the SCORE to 2 dp before
        // subtracting, so the realised movement is 20.14 − 15.00.
        Assert.Equal(0.2000m, r.CasfogProporcionMora);
        Assert.Equal(5.14m,   r.CasfogMoraAdj);
        Assert.Equal(20.14m,  r.Score);
    }

    // ── 3. Missing data never invents risk ──────────────────────────────────────────────────────

    // A null SaldoBrutoVigente is the vendor's "-" sentinel: exposure that cannot be measured. It
    // joins NEITHER sum, so it can neither inflate nor deflate the share — even when that very row is
    // the one claiming arrears.
    [Fact]
    public async Task Calculate_ARowWithNoBalance_ContributesToNeitherSum()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            casfog: Garantias((null, 9), (100_000m, 0)));

        // The only measurable balance is clean, so the share is a real 0 — not a null, and not a
        // penalty inferred from a count attached to a balance nobody can size.
        Assert.Equal(0m,     r.CasfogProporcionMora);
        Assert.Equal(0m,     r.CasfogMoraAdj);
        Assert.Equal(15.00m, r.Score);
    }

    // Every row unmeasurable → the denominator is 0 and the share is UNDEFINED, which is reported as
    // null rather than as a clean 0. "We could not measure it" and "there is none" are different
    // statements, and only the second is a clean bill of health.
    [Fact]
    public async Task Calculate_WhenNoRowCarriesAMeasurableBalance_TheShareIsUndefined()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            casfog: Garantias((null, 3), (null, 7)));

        Assert.Null(r.CasfogProporcionMora);
        Assert.Equal(0m,     r.CasfogMoraAdj);
        Assert.Equal(15.00m, r.Score);
    }

    // A zero or negative balance is not exposure either. The negative case matters beyond tidiness: an
    // unguarded sum could zero the denominator, or invert the ratio.
    [Theory]
    [InlineData(0)]
    [InlineData(-500000)]
    public async Task Calculate_ARowWithANonPositiveBalance_ContributesToNeitherSum(int saldo)
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            casfog: Garantias(((decimal)saldo, 5), (200_000m, 0)));

        Assert.Equal(0m,     r.CasfogProporcionMora);
        Assert.Equal(0m,     r.CasfogMoraAdj);
        Assert.Equal(15.00m, r.Score);
    }

    // A null CantidadEnMora is a count that did not parse, NOT a report of arrears. The row still
    // carries measurable exposure, so it counts toward the denominator — but never the numerator.
    [Fact]
    public async Task Calculate_ARowWithNoArrearsCount_IsNotTreatedAsInArrears()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            casfog: Garantias((500_000m, null), (500_000m, 0)));

        Assert.Equal(0m,     r.CasfogProporcionMora);
        Assert.Equal(0m,     r.CasfogMoraAdj);
        Assert.Equal(15.00m, r.Score);
    }

    // A count of exactly 0 is an SGR affirming it has nothing in arrears — the denominator, never the
    // numerator. Pinned separately from the null case because the two arrive by different routes.
    [Fact]
    public async Task Calculate_ARowReportingZeroArrears_IsNotTreatedAsInArrears()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            casfog: Garantias((300_000m, 0), (700_000m, 0)));

        Assert.Equal(0m,     r.CasfogProporcionMora);
        Assert.Equal(0m,     r.CasfogMoraAdj);
        Assert.Equal(15.00m, r.Score);
    }

    // ── 4. Absent CASFOG data is a zero penalty, never a throw ──────────────────────────────────

    // The three ways an entity reaches scoring with nothing usable: no snapshot at all (a tenant whose
    // profile disables CASFOG never persists one), a snapshot whose lookup failed or reported no data
    // — both of which BuildEmpty leaves with an EMPTY PorSgr list — and a blob that would not parse,
    // which the controller turns into a null before the engine ever sees it.
    [Fact]
    public async Task Calculate_WithNoCasfogRowsAtAll_AppliesNoPenaltyAndScoresAsBefore()
    {
        var sut = await BuildAsync();

        var sinSnapshot = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), casfog: null);
        var sinFilas    = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            casfog: new List<CasfogGarantiaSgr>());

        foreach (var r in new[] { sinSnapshot, sinFilas })
        {
            Assert.Null(r.CasfogProporcionMora);
            Assert.Equal(0m, r.CasfogMoraAdj);

            // The score an analysis with no CASFOG data gets is exactly the one it got before this
            // signal existed. That is what makes the signal safe to ship to every tenant at once.
            Assert.Equal(15.00m, r.Score);
            Assert.Equal("Low",  r.Level);
        }
    }

    // CalculateAsync is contractually never-throws, but a malformed snapshot must cost the analysis
    // its arrears signal and NOTHING else. Falling into the catch-all would replace a good composite
    // with the neutral 50, so asserting 15.00 — not 50 — is the point of this test.
    [Fact]
    public async Task Calculate_WithAMalformedRowSet_CostsOnlyTheSignal_NotTheWholeScore()
    {
        var sut = await BuildAsync();

        // A null element inside the list is the shape a hand-edited or truncated blob deserialises to.
        var filas = new List<CasfogGarantiaSgr> { null!, new() { SaldoBrutoVigente = null, CantidadEnMora = 4 } };

        var r = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), casfog: filas);

        Assert.Equal(0m,     r.CasfogMoraAdj);
        Assert.Equal(15.00m, r.Score);
        Assert.Equal("11",    r.ScoringAlgoVersion);   // v11: the acid-test floor needs a weak current ratio (v10: zero current liabilities at the best band + profitability floor needs an operating loss)
    }

    // ── 5. The CASFOG situación scale is never read ─────────────────────────────────────────────

    // CASFOG grades debt on its own 0-10 scale; this repo's BCRA situación is a distinct 1-6 one, and
    // conflating them would be a category error with a plausible-looking result. The harness populates
    // SituacionDeuda and DiasAtraso on every row precisely so this can be asserted: two portfolios
    // identical in balance and arrears count must score identically no matter what those fields say.
    [Fact]
    public async Task Calculate_IgnoresSituacionDeudaAndDiasAtraso_Entirely()
    {
        var sut = await BuildAsync();

        var conSituacionBenigna = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            casfog: new List<CasfogGarantiaSgr>
            {
                new() { SaldoBrutoVigente = 190_000m, CantidadEnMora = 1, SituacionDeuda = 1,  DiasAtraso = 0    },
                new() { SaldoBrutoVigente = 810_000m, CantidadEnMora = 0, SituacionDeuda = 1,  DiasAtraso = 0    },
            });

        var conSituacionGrave = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            casfog: new List<CasfogGarantiaSgr>
            {
                new() { SaldoBrutoVigente = 190_000m, CantidadEnMora = 1, SituacionDeuda = 10, DiasAtraso = 3600 },
                new() { SaldoBrutoVigente = 810_000m, CantidadEnMora = 0, SituacionDeuda = 10, DiasAtraso = 3600 },
            });

        Assert.Equal(0.1900m, conSituacionBenigna.CasfogProporcionMora);
        Assert.Equal(4.80m,   conSituacionBenigna.CasfogMoraAdj);

        Assert.Equal(conSituacionBenigna.CasfogProporcionMora, conSituacionGrave.CasfogProporcionMora);
        Assert.Equal(conSituacionBenigna.CasfogMoraAdj,        conSituacionGrave.CasfogMoraAdj);
        Assert.Equal(conSituacionBenigna.Score,                conSituacionGrave.Score);
    }

    // The BCRA situación logic is untouched by this change: the same debt situación still produces the
    // same BCRA component, with or without CASFOG rows beside it.
    [Fact]
    public async Task Calculate_TheBcraSituacionLogic_IsUnaffectedByTheArrearsSignal()
    {
        var sut = await BuildAsync();

        var sinCasfog = await Score(sut, BaselineRatios(), BcraDebtFound(3), MacroBenign());
        var conCasfog = await Score(sut, BaselineRatios(), BcraDebtFound(3), MacroBenign(),
            casfog: GarantiasProporcion(0.40m));

        Assert.Equal(sinCasfog.ScoreBcra, conCasfog.ScoreBcra);
        Assert.Equal(sinCasfog.Score + Plena, conCasfog.Score);
    }

    // ── 6. The per-tenant switch ────────────────────────────────────────────────────────────────

    // Off means never evaluated: adjustment zero AND the reported share null, exactly like an entity
    // with no rows at all. This is the per-signal switch — scoring does not consult the tenant profile,
    // so this key is how a CASFOG-enabled tenant turns just this signal off.
    [Fact]
    public async Task Calculate_WhenTheTenantDisablesTheSignal_NoPenaltyIsApplied()
    {
        var sut = await BuildAsync(("casfog_mora_enabled", 0m));

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            casfog: GarantiasProporcion(0.80m));

        Assert.Equal(0m, r.CasfogMoraAdj);
        Assert.Null(r.CasfogProporcionMora);
        Assert.Equal(15.00m, r.Score);
    }

    // Config rows are per-tenant: another tenant's disable must not reach this one.
    [Fact]
    public async Task Calculate_AnotherTenantsDisableRow_DoesNotReachThisTenant()
    {
        var sut = await BuildFromRowsAsync(Row(OtherTenant, "casfog_mora_enabled", 0m));

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            casfog: GarantiasProporcion(0.40m));

        Assert.Equal(Plena, r.CasfogMoraAdj);
    }

    // ── 7. Seeded parameters and the shipped defaults ───────────────────────────────────────────

    [Fact]
    public async Task Calculate_WithSeededParameters_UsesThemInsteadOfTheDefaults()
    {
        // A steeper, earlier ramp: nothing below 10 %, full 20 points at 30 %.
        var sut = await BuildAsync(
            ("casfog_mora_penalizacion_plena", 20m),
            ("casfog_mora_umbral_inicio",      0.10m),
            ("casfog_mora_umbral_plena",       0.30m));

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            casfog: GarantiasProporcion(0.20m));   // the midpoint of 0.10 → 0.30

        Assert.Equal(0.2000m, r.CasfogProporcionMora);
        Assert.Equal(10.00m,  r.CasfogMoraAdj);
        Assert.Equal(25.00m,  r.Score);
    }

    // A degenerate config whose thresholds meet must not divide by zero. The full-penalty arm is
    // tested first, so it collapses to a STEP at that point rather than to a silent zero.
    [Fact]
    public async Task Calculate_WhenTheTwoThresholdsMeet_TheRampCollapsesToAStep()
    {
        var sut = await BuildAsync(
            ("casfog_mora_umbral_inicio", 0.20m),
            ("casfog_mora_umbral_plena",  0.20m));

        var justo  = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            casfog: GarantiasProporcion(0.20m));
        var debajo = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            casfog: GarantiasProporcion(0.19m));

        Assert.Equal(Plena, justo.CasfogMoraAdj);
        Assert.Equal(0m,    debajo.CasfogMoraAdj);
    }

    // The engine clamps as a second layer behind SettingsService: an out-of-range plena stored by a
    // DBA straight into ScoringConfig cannot carry the score off the 0-100 scale.
    [Fact]
    public async Task Calculate_ClampsAnOutOfRangePlena()
    {
        var sut = await BuildAsync(("casfog_mora_penalizacion_plena", 500m));

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            casfog: GarantiasProporcion(0.40m));

        // plena clamped to 100, so the score saturates at 100 and the REALISED movement is 85.00 —
        // not the 100 the config asked for, and not the 500 it was stored as.
        Assert.Equal(100.00m, r.Score);
        Assert.Equal(85.00m,  r.CasfogMoraAdj);
    }

    // A threshold outside [0,1] is clamped too. inicio 5 clamps to 1 and plena 9 to 1, which meets
    // them at the top of the scale: only a portfolio entirely in arrears is penalised, and the step
    // arm — not a divide by zero — is what produces that.
    [Fact]
    public async Task Calculate_ClampsOutOfRangeThresholds()
    {
        var sut = await BuildAsync(
            ("casfog_mora_umbral_inicio", 5m),
            ("casfog_mora_umbral_plena",  9m));

        var total  = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            casfog: GarantiasProporcion(1.00m));
        var parcial = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            casfog: GarantiasProporcion(0.90m));

        Assert.Equal(Plena, total.CasfogMoraAdj);
        Assert.Equal(0m,    parcial.CasfogMoraAdj);
    }

    // The shipped defaults are part of the contract: a change to any of them is a calibration
    // decision, not an incidental edit, and must break this test on the way through.
    [Fact]
    public void ShippedDefaults_AreTheOnesThisSuiteAssertsAgainst()
    {
        Assert.Equal(12.0000m, ScoringDefaults.Get("casfog_mora_penalizacion_plena"));
        Assert.Equal( 0.0500m, ScoringDefaults.Get("casfog_mora_umbral_inicio"));
        Assert.Equal( 0.4000m, ScoringDefaults.Get("casfog_mora_umbral_plena"));
        Assert.Equal( 1.0000m, ScoringDefaults.Get("casfog_mora_enabled"));

        // The ramp's width, which every fraction in this file divides by.
        Assert.Equal(0.3500m,
            ScoringDefaults.Get("casfog_mora_umbral_plena") - ScoringDefaults.Get("casfog_mora_umbral_inicio"));
    }

    // ── 8. The realised movement, after the final clamp ─────────────────────────────────────────

    // CasfogMoraAdj reports the points the score ACTUALLY took, exactly as ventasAdj and chequesAdj
    // do. A score pushed to the top of the scale absorbs the rest, and the PDF and the chat context
    // must never claim a penalty the score never carried. Driven by a seeded plena rather than by
    // extreme ratios so the saturation point is arithmetic, not a guess about another fixture.
    [Fact]
    public async Task Calculate_WhenTheScoreSaturates_ReportsOnlyTheRealisedPoints()
    {
        var sut = await BuildAsync(("casfog_mora_penalizacion_plena", 90m));

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            casfog: GarantiasProporcion(0.40m));

        // 15.00 + 90 would be 105; the score stops at 100 and reports the 85.00 it really took.
        Assert.Equal(100.00m, r.Score);
        Assert.Equal(85.00m,  r.CasfogMoraAdj);
        Assert.True(r.CasfogMoraAdj < 90m);
    }

    // The two final-score adjustments compose without interfering: applied in sequence, each reports
    // its own realised movement and the score carries both.
    [Fact]
    public async Task Calculate_WithBothFinalScoreAdjustments_EachReportsItsOwnRealisedMovement()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            ventas: VentasInteranual(-0.35m),        // full sales plena: 10.00
            casfog: GarantiasProporcion(0.40m));     // full arrears plena: 12.00

        Assert.Equal(10.00m, r.VentasAdj);
        Assert.Equal(12.00m, r.CasfogMoraAdj);
        Assert.Equal(37.00m, r.Score);               // 15.00 + 10.00 + 12.00
    }

    // ── 9. Determinism ──────────────────────────────────────────────────────────────────────────

    // The engine reads no clock and the arrears share is a pure function of the stored snapshot, so
    // the same rows scored in any era give the same penalty — the property algorithm version 2
    // established for the BCRA trend window, which every signal since has had to preserve.
    [Fact]
    public async Task Calculate_TheSameSnapshotScoredInAnyEra_YieldsTheSamePenalty()
    {
        var sut = await BuildAsync();

        var filas = Garantias((330_000m, 2), (670_000m, 0));

        var a = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), casfog: filas);
        var b = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), casfog: filas);
        var c = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), casfog: filas);

        Assert.Equal(0.3300m, a.CasfogProporcionMora);
        Assert.Equal(9.60m,   a.CasfogMoraAdj);

        Assert.Equal(a.CasfogMoraAdj,        b.CasfogMoraAdj);
        Assert.Equal(a.CasfogProporcionMora, b.CasfogProporcionMora);
        Assert.Equal(a.Score,                b.Score);
        Assert.Equal(a.CasfogMoraAdj,        c.CasfogMoraAdj);
        Assert.Equal(a.Score,                c.Score);
    }

    // Nothing re-sorts a persisted blob on read, so the engine must not depend on the order the rows
    // were written in. Both sums are order-independent, and this pins that.
    [Fact]
    public async Task Calculate_TheSameRowsInAnyOrder_YieldTheSamePenalty()
    {
        var sut = await BuildAsync();

        var enOrden   = Garantias((120_000m, 1), (380_000m, 0), (500_000m, 0));
        var invertido = Enumerable.Reverse(enOrden).ToList();

        var a = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), casfog: enOrden);
        var b = await Score(sut, BaselineRatios(), BcraNoDebt(), MacroBenign(), casfog: invertido);

        Assert.Equal(0.1200m, a.CasfogProporcionMora);
        Assert.Equal(a.CasfogProporcionMora, b.CasfogProporcionMora);
        Assert.Equal(a.CasfogMoraAdj,        b.CasfogMoraAdj);
    }

    // ── 10. The algorithm version ───────────────────────────────────────────────────────────────

    // The arrears penalty changes the engine's arithmetic, so it bumps the algorithm version.
    // Analyses scored before it are NOT recomputed: the stamp is what keeps an old score identifiable
    // as one an earlier engine produced. Asserted against the literal AND the constant, as the
    // cheques, BCRA and ventas suites do — the literal makes a future bump deliberate.
    [Fact]
    public async Task Calculate_WithTheCasfogMoraPenalty_StampsAlgorithmVersionEight()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraNoDebt(), MacroBenign(),
            casfog: GarantiasProporcion(0.19m));

        Assert.Equal("11", r.ScoringAlgoVersion);   // v11: the acid-test floor needs a weak current ratio (v10: zero current liabilities at the best band + profitability floor needs an operating loss)
        Assert.Equal(ScoringService.ScoringAlgoVersion, r.ScoringAlgoVersion);
    }
}
