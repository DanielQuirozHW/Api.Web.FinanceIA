// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-05

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.ScoringTestHarness;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Pins the rejected-cheques penalty (§7.1, scoring algorithm version 5): a bounded adjustment INSIDE
/// the BCRA component, sibling of the trend adjustment and applied after it, never a fourth weighted
/// component.
/// <para>
/// The rule is <c>chequesAdj = plena × (1 − proporciónRecuperada)</c>, where recovery is measured by
/// AMOUNT — <c>(MontoTotalRechazado − MontoTotalImpago) / MontoTotalRechazado</c> — and never by the
/// per-cheque <c>Detalle</c> list, which the engine never reads. The penalty RAISES risk, so it is
/// ADDED to scoreBcra and the existing 0–100 clamp applies; like <c>BcraTrendAdj</c>, the reported
/// figure is the movement that actually happened, not the one the configuration proposed.
/// </para>
/// <para>
/// The load-bearing negative cases are the three ways there is nothing to penalise. A failed cheques
/// lookup, an absent cheques block and a genuinely clean record all produce ZERO — the first because
/// the engine must never invent risk out of missing data (the same refusal the BCRA exclusion makes),
/// the last because a clean record is good news. They are not interchangeable inputs: BcraService
/// returns the failed and the clean block with byte-identical zero amounts, so the engine gates on
/// <c>Estado</c> BEFORE the amounts, and a regression to an amounts-first test would silently score
/// every BCRA outage as a spotless cheque history.
/// </para>
/// <para>
/// Every fixture uses <see cref="ScoringTestHarness.BaselineRatios"/> (scoreRatios exactly 21.00, no
/// partial near the critical trigger, 100 since v9) and <c>MacroBenign</c> (scoreMacro 30), so with the nominal
/// 0.50 / 0.35 / 0.15 component weights the composite is always
/// <c>Score = 21.00×0.50 + scoreBcra×0.35 + 30×0.15 = 15.00 + 0.35 × scoreBcra</c>.
/// </para>
/// </summary>
public sealed class ScoringServiceChequesTests
{
    // The shipped default: nothing recovered costs 15 points of the 0-100 BCRA scale.
    private const decimal Plena = 15.00m;

    // ── 1. The penalty scales linearly with the RECOVERED SHARE OF THE AMOUNT ───────────────────
    //
    // Situación 3 → base 45, empty historial so the trend block cannot fire and the cheques
    // adjustment is observed alone. Same rejected total in every row; only the unpaid part moves.
    //
    //   recovered   proporción   adj = 15 × (1 − p)   scoreBcra   Score = 15.00 + 0.35 × scoreBcra
    //        0 %          0.0                 15.00          60                             36.00
    //       60 %          0.6                  6.00          51                             32.85
    //      100 %          1.0                  0.00          45                             30.75

    // Amounts travel as doubles because [InlineData] cannot carry a decimal literal — the same reason
    // SettingsServiceScoringValidationTests passes its values as strings. Every figure below is exact
    // in both types, so the conversion cannot move a boundary.
    [Theory]
    [InlineData(100_000d, 100_000d, 0.0,  15.00, 60.00, 36.00, "Medium")]   // nothing recovered → full plena
    [InlineData(100_000d,  40_000d, 0.6,   6.00, 51.00, 32.85, "Low")]      // 60 % recovered → 40 % of plena
    [InlineData(100_000d,       0d, 1.0,   0.00, 45.00, 30.75, "Low")]      // all recovered → no penalty
    public async Task Calculate_ChequesPenalty_ScalesWithTheRecoveredAmount(
        double montoRechazado,
        double montoImpago,
        double expectedProporcion,
        double expectedAdj,
        double expectedScoreBcra,
        double expectedScore,
        string expectedLevel)
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(),
            BcraDebtFound(3, cheques: ChequesFound((decimal)montoRechazado, (decimal)montoImpago)),
            MacroBenign());

        Assert.Equal((decimal)expectedProporcion, r.ChequesProporcionRecuperada);
        Assert.Equal((decimal)expectedAdj,        r.ChequesAdj);
        Assert.Equal((decimal)expectedScoreBcra,  r.ScoreBcra);
        Assert.Equal((decimal)expectedScore,      r.Score);
        Assert.Equal(expectedLevel,               r.Level);

        // Inside the BCRA component only: the 0.50 / 0.35 / 0.15 split is untouched, which is what
        // makes this a penalty rather than a fourth component.
        Assert.Equal(0.5000m, r.EffectiveWeightRatios);
        Assert.Equal(0.3500m, r.EffectiveWeightBcra);
        Assert.Equal(0.1500m, r.EffectiveWeightMacro);
        Assert.Equal(21.00m,  r.ScoreRatios);
        Assert.False(r.BcraExcluded);
    }

    // The case the penalty most exists for, and the one a DebtFound-only placement would miss: a
    // company with NO bank debt — scoreBcra pinned at the optimal 0 — but rejected cheques nobody has
    // made good on. Without this the cleanest-looking BCRA profile absorbs the signal entirely.
    [Fact]
    public async Task Calculate_ChequesPenalty_AppliesOnTheNoDebtReportedPath()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(),
            BcraNoDebt(cheques: ChequesFound(montoRechazado: 250_000m, montoImpago: 250_000m)),
            MacroBenign());

        Assert.Equal(0m,      r.ChequesProporcionRecuperada);
        Assert.Equal(Plena,   r.ChequesAdj);
        Assert.Equal(15.00m,  r.ScoreBcra);    // 0 base + the full penalty
        Assert.Equal(20.25m,  r.Score);        // 15.00 + 0.35 × 15
        Assert.False(r.BcraExcluded);
    }

    // ── 2. Nothing to penalise → exactly zero, three different ways ─────────────────────────────
    //
    // NoChequesReported is a clean record; LookupFailed is an outage; a null block is an analysis
    // whose payload carries no cheques at all (a legacy row, or a fetch that never happened). All
    // three must leave the score identical to the no-cheques baseline — and the two Estado values
    // arrive with the SAME zeroed amounts, which is why Estado is what the engine reads.

    [Theory]
    [InlineData("no cheques block at all")]
    [InlineData("clean record")]
    [InlineData("failed lookup")]
    public async Task Calculate_WhenThereIsNothingToPenalise_TheScoreIsUntouched(string shape)
    {
        // The block is built here rather than passed in: a BcraChequesData is not xUnit-serializable,
        // so carrying it through [InlineData] would cost a warning and tell the reader less than the
        // three names do.
        BcraChequesData? cheques = shape switch
        {
            "clean record"  => ChequesEmpty(BcraChequesConsultaEstado.NoChequesReported),
            "failed lookup" => ChequesEmpty(BcraChequesConsultaEstado.LookupFailed),
            _               => null,
        };

        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(), BcraDebtFound(3, cheques: cheques), MacroBenign());

        Assert.Equal(0m, r.ChequesAdj);
        Assert.Null(r.ChequesProporcionRecuperada);   // the penalty was not evaluated at all
        Assert.Equal(45.00m, r.ScoreBcra);            // the raw situación-3 value
        Assert.Equal(30.75m, r.Score);
    }

    // ChequesFound with no rejected amount: nothing to penalise, and the divisor of the proportion.
    // BcraService only reaches ChequesFound with TotalRechazados > 0, so this is a stored or
    // hand-edited blob — the guard is here so it cannot divide by zero either way.
    [Fact]
    public async Task Calculate_WhenChequesFoundCarriesNoRejectedAmount_AppliesNoPenalty()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(),
            BcraDebtFound(3, cheques: ChequesFound(montoRechazado: 0m, montoImpago: 0m)),
            MacroBenign());

        Assert.Equal(0m, r.ChequesAdj);
        Assert.Null(r.ChequesProporcionRecuperada);
        Assert.Equal(45.00m, r.ScoreBcra);
        Assert.Equal(30.75m, r.Score);
    }

    // Malformed data: more unpaid than was ever rejected. The raw proportion is negative, which would
    // make the penalty EXCEED plena. Clamped into [0,1], so the worst case is exactly the full plena —
    // the penalty is bounded by its own parameter no matter what the blob says.
    [Fact]
    public async Task Calculate_WhenImpagoExceedsRechazado_ClampsTheProportionAndCapsThePenalty()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(),
            BcraDebtFound(3, cheques: ChequesFound(montoRechazado: 100_000m, montoImpago: 150_000m)),
            MacroBenign());

        Assert.Equal(0m,     r.ChequesProporcionRecuperada);   // −0.5 clamped up to 0
        Assert.Equal(Plena,  r.ChequesAdj);                    // never more than the full penalty
        Assert.Equal(60.00m, r.ScoreBcra);
        Assert.Equal(36.00m, r.Score);
    }

    // ── 3. The clamp, and what the reported adjustment means ────────────────────────────────────
    //
    // Mirrors ScoringServiceBcraTests' pair for the trend adjustment: the reported figure is what
    // MOVED, not what was proposed. Situación 6 is already at the 100 ceiling of the BCRA scale, so
    // the whole 15-point penalty is absorbed and the honest report is zero.

    [Fact]
    public async Task Calculate_WhenTheClampAbsorbsThePenalty_ReportsZeroPointsApplied()
    {
        var sut = await BuildAsync();

        var withCheques = await Score(
            sut, BaselineRatios(),
            BcraDebtFound(6, cheques: ChequesFound(500_000m, 500_000m)), MacroBenign());

        var withoutCheques = await Score(sut, BaselineRatios(), BcraDebtFound(6), MacroBenign());

        Assert.Equal(0m, withCheques.ChequesAdj);
        Assert.Equal(withoutCheques.ScoreBcra, withCheques.ScoreBcra);
        Assert.Equal(withoutCheques.Score,     withCheques.Score);
        Assert.Equal(100.00m, withCheques.ScoreBcra);
        Assert.Equal(50.00m,  withCheques.Score);

        // The proportion is still reported: the penalty WAS evaluated (and found 0 % recovered), it
        // simply had nowhere left to move the score. That is a different fact from "not evaluated",
        // which the three no-penalty inputs above report as null.
        Assert.Equal(0m, withCheques.ChequesProporcionRecuperada);
    }

    // The partial-absorption mirror, so a fix that reports 0 whenever ANY clamping occurred fails:
    // situación 5 sits at 90 and can only take the last 10 of the 15 proposed points.
    [Fact]
    public async Task Calculate_WhenTheClampAbsorbsPartOfThePenalty_ReportsOnlyWhatMoved()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(),
            BcraDebtFound(5, cheques: ChequesFound(500_000m, 500_000m)), MacroBenign());

        Assert.Equal(10.00m,  r.ChequesAdj);    // proposed 15, realised 90 → 100
        Assert.Equal(100.00m, r.ScoreBcra);
        Assert.Equal(50.00m,  r.Score);
    }

    // ── 4. Living beside the trend adjustment ───────────────────────────────────────────────────
    //
    // Both adjustments on one analysis. The cheques penalty is applied AFTER the trend, on the score
    // the trend left, and both report realised points — so the three numbers reconcile exactly:
    //   base 20 (situación 2) + trend +8 (one step worse) + cheques +9 (40 % recovered) = 37.
    // A future change that reorders them, or reports a proposed figure for either, breaks this sum.
    [Fact]
    public async Task Calculate_TrendAndChequesTogether_BothReportRealisedPointsAndSumToScoreBcra()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(),
            BcraDebtFound(2,
                Historial(("190012", 1), ("190006", 1)),
                ChequesFound(montoRechazado: 100_000m, montoImpago: 60_000m)),
            MacroBenign());

        Assert.Equal(1,      r.BcraTrendDelta);   // situación 2 today vs 1 six months back
        Assert.Equal(8.00m,  r.BcraTrendAdj);     // min(1 × 8, 30)
        Assert.Equal(0.4m,   r.ChequesProporcionRecuperada);
        Assert.Equal(9.00m,  r.ChequesAdj);       // 15 × (1 − 0.4)

        const decimal situacion2Base = 20.00m;
        Assert.Equal(situacion2Base + r.BcraTrendAdj + r.ChequesAdj, r.ScoreBcra);
        Assert.Equal(37.00m, r.ScoreBcra);
        Assert.Equal(27.95m, r.Score);            // 15.00 + 0.35 × 37
    }

    // ── 5. BCRA excluded: the penalty has no score to move ──────────────────────────────────────
    //
    // When the Central de Deudores lookup fails the whole BCRA component is dropped and its weight
    // renormalised onto ratios and macro; ScoreBcra is reported as 0 and carries no weight. Applying
    // a cheques penalty there would compute points that reach nothing and report an adjustment the
    // composite never took. The three exclusion reasons stay exactly three — cheques never adds one.
    [Fact]
    public async Task Calculate_WhenBcraIsExcluded_NoChequesPenaltyIsApplied()
    {
        var sut = await BuildAsync();

        var excluded = await Score(
            sut, BaselineRatios(),
            BcraDeudaLookupFailed(ChequesFound(500_000m, 500_000m)), MacroBenign());

        var excludedNoCheques = await Score(
            sut, BaselineRatios(), BcraDeudaLookupFailed(), MacroBenign());

        Assert.True(excluded.BcraExcluded);
        Assert.Equal(BcraExclusionReasons.LookupFailed, excluded.BcraExclusionReason);

        Assert.Equal(0m, excluded.ChequesAdj);
        Assert.Null(excluded.ChequesProporcionRecuperada);
        Assert.Equal(0m, excluded.ScoreBcra);
        Assert.Equal(0.0000m, excluded.EffectiveWeightBcra);

        // Indistinguishable from the same analysis with no cheques at all.
        Assert.Equal(excludedNoCheques.Score, excluded.Score);
        Assert.Equal(23.08m, excluded.Score);
    }

    // ── 6. Tenant configuration ─────────────────────────────────────────────────────────────────

    // The full penalty is a per-tenant ScoringConfig parameter, read through the same
    // Get(cfg, key, ScoringDefaults.Get(key)) path as every other parameter: a seeded row changes the
    // outcome, and the shipped default is 15.
    [Fact]
    public async Task Calculate_ChequesPenalty_HonoursTheSeededPlena()
    {
        var sut = await BuildAsync(("bcra_cheques_penalizacion_plena", 30m));

        var r = await Score(
            sut, BaselineRatios(),
            BcraDebtFound(3, cheques: ChequesFound(100_000m, 100_000m)), MacroBenign());

        Assert.Equal(30.00m, r.ChequesAdj);     // the seeded plena, not the default 15
        Assert.Equal(75.00m, r.ScoreBcra);
        Assert.Equal(41.25m, r.Score);
    }

    [Fact]
    public void ChequesPenalty_DefaultPlena_IsFifteenPoints()
    {
        Assert.Equal(Plena, ScoringDefaults.Get("bcra_cheques_penalizacion_plena"));
        Assert.Equal(1.0000m, ScoringDefaults.Get("bcra_cheques_enabled"));
    }

    // The signal is switchable per tenant — the gate §7.0's tenant profile does not provide, since
    // BCRA is deliberately un-keyed there. Off means the penalty is never evaluated: adjustment zero
    // AND proportion null, exactly like an analysis with no cheques block.
    [Fact]
    public async Task Calculate_WhenTheTenantDisablesTheSignal_NoPenaltyIsApplied()
    {
        var sut = await BuildAsync(("bcra_cheques_enabled", 0m));

        var r = await Score(
            sut, BaselineRatios(),
            BcraDebtFound(3, cheques: ChequesFound(100_000m, 100_000m)), MacroBenign());

        Assert.Equal(0m, r.ChequesAdj);
        Assert.Null(r.ChequesProporcionRecuperada);
        Assert.Equal(45.00m, r.ScoreBcra);
        Assert.Equal(30.75m, r.Score);
    }

    // Config rows are per-tenant: another tenant's disable must not reach this one.
    [Fact]
    public async Task Calculate_AnotherTenantsDisableRow_DoesNotReachThisTenant()
    {
        var sut = await BuildFromRowsAsync(Row(OtherTenant, "bcra_cheques_enabled", 0m));

        var r = await Score(
            sut, BaselineRatios(),
            BcraDebtFound(3, cheques: ChequesFound(100_000m, 100_000m)), MacroBenign());

        Assert.Equal(Plena, r.ChequesAdj);
    }

    // ── 7. Determinism and the version stamp ────────────────────────────────────────────────────

    // The penalty reads Estado and two stored totals and nothing else — no clock, no Detalle list — so
    // the same payload scores identically however often it is scored. This is the contract the v2 bump
    // was made to restore, and the cheques signal must not be where it leaks back in.
    [Fact]
    public async Task Calculate_ChequesPenalty_IsAPureFunctionOfTheStoredBlock()
    {
        var sut = await BuildAsync();

        var cheques = ChequesFound(100_000m, 40_000m);
        cheques.FetchedAt          = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        cheques.FechaPrimerRechazo = new DateTime(2001, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        cheques.FechaUltimoRechazo = new DateTime(2001, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        var chequesFuture = ChequesFound(100_000m, 40_000m);
        chequesFuture.FetchedAt          = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        chequesFuture.FechaPrimerRechazo = new DateTime(2099, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        chequesFuture.FechaUltimoRechazo = new DateTime(2099, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        var rPast   = await Score(sut, BaselineRatios(), BcraDebtFound(3, cheques: cheques),       MacroBenign());
        var rFuture = await Score(sut, BaselineRatios(), BcraDebtFound(3, cheques: chequesFuture), MacroBenign());
        var rAgain  = await Score(sut, BaselineRatios(), BcraDebtFound(3, cheques: cheques),       MacroBenign());

        Assert.Equal(rPast.ChequesAdj, rFuture.ChequesAdj);
        Assert.Equal(rPast.Score,      rFuture.Score);
        Assert.Equal(rPast.ChequesAdj, rAgain.ChequesAdj);
        Assert.Equal(rPast.Score,      rAgain.Score);
        Assert.Equal(6.00m, rPast.ChequesAdj);
    }

    // The cheques penalty is the first change to the engine's arithmetic in the scoring redesign, so
    // it bumps the algorithm version. Analyses scored before it are NOT recomputed: the stamp is what
    // keeps an old score identifiable as one an earlier engine produced. Asserted against the literal
    // AND the constant, as ScoringServiceBcraTests does — the literal makes a future bump deliberate.
    [Fact]
    public async Task Calculate_WithTheChequesPenalty_StampsAlgorithmVersionFive()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(),
            BcraDebtFound(3, cheques: ChequesFound(100_000m, 40_000m)), MacroBenign());

        Assert.Equal("11", r.ScoringAlgoVersion);   // v11: the acid-test floor needs a weak current ratio (v10: zero current liabilities at the best band + profitability floor needs an operating loss)
        Assert.Equal(ScoringService.ScoringAlgoVersion, r.ScoringAlgoVersion);
    }
}
