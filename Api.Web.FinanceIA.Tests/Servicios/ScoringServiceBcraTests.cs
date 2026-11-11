// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-01

using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.ScoringTestHarness;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Pins the BCRA half of the engine: the situación → score map, and the whole trend-adjustment block
/// (Feature 5c).
/// <para>
/// GAP THIS FILLS: <see cref="ScoringServiceTests"/> pins situación 3 → 45 and nothing else on the
/// map, and it deliberately leaves <c>HistorialSituaciones</c> EMPTY in every fixture so the trend
/// block never fires — meaning that block had never executed under test at all. Its guards, its two
/// per-step multipliers, both Math.Min caps, the Math.Clamp and the comparison-period selection were
/// all unexercised.
/// </para>
/// <para>
/// EVERY fixture here uses <see cref="ScoringTestHarness.BaselineRatios"/> (all eight computable,
/// scoreRatios exactly 21.00, no partial near the critical trigger) and <c>MacroBenign</c>
/// (scoreMacro 30), so with the nominal 0.50 / 0.35 / 0.15 component weights the composite is always
/// <c>Score = 21.00*0.50 + scoreBcra*0.35 + 30*0.15 = 15.00 + 0.35 * scoreBcra</c>.
/// Every expectation below is that one expression.
/// </para>
/// <para>
/// DATE INDEPENDENCE. The trend cutoff is <c>anchor - 6 months</c>, where the anchor is the ordinal
/// MAX period in the fixture's own history — the engine reads no clock (finding D2). Every fixture is
/// therefore date-independent by construction, and the sentinel years these tests once needed
/// ("1900xx" at or below a sliding cutoff, "9999xx" above it) no longer carry any meaning. What
/// decides the branch now is the history's own SPAN: at least six months back from its newest period
/// exercises the normal selection, less than that falls through to <c>?? historial[^1]</c>. The
/// periods below were chosen for the branch each test pins, and
/// <see cref="Calculate_BcraTrend_IsInvariantUnderTheCalendar_AndUnderRepeatedScoring"/> pins the
/// date-independence itself so a regression to <c>DateTime.UtcNow</c> fails loudly.
/// </para>
/// </summary>
public sealed class ScoringServiceBcraTests
{
    // ── 1. The situación → score map ────────────────────────────────────────────────────────────
    //
    // Situación 3 → 45 → 30.75 is already pinned by Calculate_BcraIncluded_PinsCalibration and is
    // deliberately not repeated. Everything else on the map is pinned here for the first time.
    // Empty historial, so the trend block cannot fire and the map is observed directly.

    [Theory]
    [InlineData(1,   0.0, 15.00, "Low")]
    [InlineData(2,  20.0, 22.00, "Low")]
    [InlineData(4,  70.0, 39.50, "Medium")]
    [InlineData(5,  90.0, 46.50, "Medium")]
    [InlineData(6, 100.0, 50.00, "Medium")]
    [InlineData(7,  50.0, 32.50, "Low")]   // out of range → the switch's `_ => 50m` default
    [InlineData(0,  50.0, 32.50, "Low")]   // ditto below the range
    public async Task Calculate_BcraSituacionMap_ScoresEachLevel(
        int situacion, double expectedScoreBcra, double expectedScore, string expectedLevel)
    {
        var sut = await BuildAsync();

        var r = await Score(sut, BaselineRatios(), BcraDebtFound(situacion), MacroBenign());

        Assert.Equal(21.00m, r.ScoreRatios);                        // the ratio half is inert
        Assert.Equal((decimal)expectedScoreBcra, r.ScoreBcra);
        Assert.Equal((decimal)expectedScore, r.Score);
        Assert.Equal(expectedLevel, r.Level);

        Assert.False(r.BcraExcluded);
        Assert.Null(r.BcraExclusionReason);
        Assert.Equal(0,  r.BcraTrendDelta);                         // empty historial → no trend
        Assert.Equal(0m, r.BcraTrendAdj);
    }

    // DebtFound but the vendor reported no situación at all → the FALSE arm of `situacion.HasValue ?`.
    [Fact]
    public async Task Calculate_BcraDebtFoundWithNullSituacion_ScoresNeutral50()
    {
        var sut = await BuildAsync();

        var r = await Score(sut, BaselineRatios(), BcraDebtFound(situacionMaxima: null), MacroBenign());

        Assert.Equal(50.00m, r.ScoreBcra);
        Assert.Equal(32.50m, r.Score);        // 15.00 + 0.35*50
        Assert.Equal("Low", r.Level);
        Assert.False(r.BcraExcluded);
    }

    // ── 2. The trend adjustment (Feature 5c) ───────────────────────────────────────────────────
    //
    // Both history entries carry the same situación, so the comparison value is the same whichever
    // one the selection picks — that isolates the ARITHMETIC here; the SELECTION is pinned in §3.
    //
    // The pair spans exactly the six-month window, which keeps these rows on the normal selection
    // path: anchor "190012" → cutoff 1900*12 + 11 - 6 = 22805 → "190006", and "190006" <= "190006",
    // so the second entry is the comparison. (A one-month span would land on the historial[^1]
    // fallback instead — same value here, since both entries agree, but the wrong branch to pin
    // arithmetic on.)
    //
    //   delta = situacionActual - situacionComparacion
    //   delta < 0 (improved) → adj = -min(|delta| * 5, 20)
    //   delta > 0 (worsened) → adj = +min( delta  * 8, 30)
    //   scoreBcra = Clamp(base + adj, 0, 100)
    //
    //   (a) improve, uncapped    : 3 vs 5 → delta -2, adj -min(10,20) = -10 → 45-10 = 35 → 27.25
    //   (b) improve, AT the cap  : 2 vs 6 → delta -4, adj -min(20,20) = -20 → 20-20 =  0 → 15.00
    //   (c) improve, clamp EATS  : 1 vs 6 → delta -5, adj -min(25,20) = -20 →  0-20 → clamp 0 → 15.00
    //   (d) worsen,  uncapped    : 3 vs 1 → delta +2, adj +min(16,30) = +16 → 45+16 = 61 → 36.35
    //   (e) worsen,  clamp EATS  : 5 vs 1 → delta +4, adj +min(32,30) = +30 → 90+30 = 120 → clamp 100 → 50.00
    //   (f) no change            : 3 vs 3 → delta  0, no arm taken, adj 0  → 45 unchanged → 30.75
    //
    // expectedAdj is the REALISED adjustment — what scoreBcra actually moved, which is what the
    // engine reports. It equals the proposed figure only when the clamp does not bite:
    //   (c) proposes -20 onto a base of 0 → realised 0
    //   (e) proposes +30 onto a base of 90 → realised +10 (the last of the way to 100)
    // (b) and (c) are deliberately both present. The improve cap's VALUE (20) is reached at
    // |delta| >= 4, but Math.Min only DISCARDS a larger raw value at |delta| >= 5 — which forces
    // situación 1, whose base of 0 means the lower Math.Clamp fires too. So (c) alone would conflate
    // the cap with the clamp; (b) reaches the cap with the clamp provably inert (20 - 20 = 0 is
    // in range, realised -20 == proposed -20), separating the two mechanisms.

    [Theory]
    [InlineData(3, 5, -2, -10.0,  35.0, 27.25, "Low")]
    [InlineData(2, 6, -4, -20.0,   0.0, 15.00, "Low")]
    [InlineData(1, 6, -5,   0.0,   0.0, 15.00, "Low")]
    [InlineData(3, 1,  2,  16.0,  61.0, 36.35, "Medium")]
    [InlineData(5, 1,  4,  10.0, 100.0, 50.00, "Medium")]
    [InlineData(3, 3,  0,   0.0,  45.0, 30.75, "Low")]
    public async Task Calculate_BcraTrend_AdjustsScoreByStepsMoved(
        int situacion, int comparisonSituacion,
        int expectedDelta, double expectedAdj, double expectedScoreBcra,
        double expectedScore, string expectedLevel)
    {
        var sut = await BuildAsync();

        var bcra = BcraDebtFound(
            situacion,
            Historial(("190012", comparisonSituacion), ("190006", comparisonSituacion)));

        var r = await Score(sut, BaselineRatios(), bcra, MacroBenign());

        Assert.Equal(expectedDelta, r.BcraTrendDelta);
        Assert.Equal((decimal)expectedAdj, r.BcraTrendAdj);
        Assert.Equal((decimal)expectedScoreBcra, r.ScoreBcra);
        Assert.Equal((decimal)expectedScore, r.Score);
        Assert.Equal(expectedLevel, r.Level);
        Assert.False(r.BcraExcluded);
    }

    // The reported adjustment is what MOVED, not what was proposed — the fix for finding D3.
    //
    // Situación 1 already sits at the 0 floor of the BCRA scale, so the per-step configuration
    // proposes -20 and the clamp absorbs every point of it. Before the fix this reported -20.00,
    // and PdfExportService and the chat context both rendered that as "ajuste aplicado" beside a
    // score it had not touched. The two calls below must now be indistinguishable on every scoring
    // field; only BcraTrendDelta still shows that a five-step improvement happened at all.
    [Fact]
    public async Task Calculate_BcraTrend_WhenClampAbsorbsTheAdjustment_ReportsZeroPointsApplied()
    {
        var sut = await BuildAsync();

        var withTrend = await Score(
            sut, BaselineRatios(),
            BcraDebtFound(1, Historial(("190012", 6), ("190006", 6))), MacroBenign());

        var withoutTrend = await Score(sut, BaselineRatios(), BcraDebtFound(1), MacroBenign());

        // The movement in situación steps is still reported...
        Assert.Equal(-5, withTrend.BcraTrendDelta);
        Assert.Equal(0,  withoutTrend.BcraTrendDelta);

        // ...but the points claim now matches what the score actually did: nothing.
        Assert.Equal(0m, withTrend.BcraTrendAdj);
        Assert.Equal(withoutTrend.BcraTrendAdj, withTrend.BcraTrendAdj);
        Assert.Equal(withoutTrend.ScoreBcra,    withTrend.ScoreBcra);
        Assert.Equal(withoutTrend.Score,        withTrend.Score);
        Assert.Equal(0m,     withTrend.ScoreBcra);
        Assert.Equal(15.00m, withTrend.Score);
    }

    // The mirror case at the top of the scale: a proposed +30 onto a base of 90 can only deliver the
    // last 10 points. Guards against a fix that reports 0 whenever ANY clamping occurred.
    [Fact]
    public async Task Calculate_BcraTrend_WhenClampAbsorbsPartOfTheAdjustment_ReportsOnlyWhatMoved()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(),
            BcraDebtFound(5, Historial(("190012", 1), ("190006", 1))), MacroBenign());

        Assert.Equal(4, r.BcraTrendDelta);       // proposed +min(4*8, 30) = +30
        Assert.Equal(10.00m, r.BcraTrendAdj);    // realised: 90 → 100
        Assert.Equal(100.00m, r.ScoreBcra);
        Assert.Equal(50.00m, r.Score);
    }

    // ── 3. Comparison-period SELECTION ──────────────────────────────────────────────────────────

    // The block needs Count >= 2. One entry leaves scoreBcra at the raw map value.
    [Fact]
    public async Task Calculate_BcraTrend_RequiresAtLeastTwoPeriods()
    {
        var sut = await BuildAsync();

        var bcra = BcraDebtFound(3, Historial(("190006", 5)));

        var r = await Score(sut, BaselineRatios(), bcra, MacroBenign());

        Assert.Equal(0,  r.BcraTrendDelta);
        Assert.Equal(0m, r.BcraTrendAdj);
        Assert.Equal(45.00m, r.ScoreBcra);   // unadjusted situación-3 value
        Assert.Equal(30.75m, r.Score);
        // NOTE: this result is byte-identical to the delta == 0 row of the theory above. Nothing on
        // ScoringResult distinguishes "the block ran and found no change" from "the block never ran".
    }

    // The comparison is the LATEST period at or before the cutoff — an OrderByDescending, not the
    // first element of the list. This one fixture discriminates the real selection from three
    // plausible wrong ones at once, because the list is deliberately out of order and both the ANCHOR
    // and the WINNER sit in the middle of it:
    //
    //   Historial(("190012", 6), ("190101", 2), ("190107", 3), ("190011", 4)), situación actual 3
    //     anchor = ordinal MAX = "190107"          (index 2 — neither first nor last)
    //     cutoff = 1901*12 + (7-1) - 6 = 22812     → year 1901, month 1 → "190101"
    //     candidates <= cutoff = {190012, 190101, 190011}   ("190107" is the anchor, above the cutoff)
    //     winner = "190101", situación 2           → delta = 3 - 2 = +1
    //     adj = min(1*8, 30) = 8.00                → scoreBcra = 45 + 8 = 53.00
    //     Score = 15.00 + 0.35*53 = 33.55
    //
    //   anchoring on historial[0] ("190012") → cutoff "190006" → nothing at or before it → the
    //     historial[^1] fallback ("190011", 4) → delta 3-4 = -1
    //   comparison = historial[0]  ("190012", 6) → delta 3-6 = -3
    //   comparison = historial[^1] ("190011", 4) → delta 3-4 = -1
    //
    // Only ordinal-max anchoring plus the real selection yields +1.
    [Fact]
    public async Task Calculate_BcraTrend_ComparesAgainstLatestPeriodAtOrBeforeCutoff()
    {
        var sut = await BuildAsync();

        var bcra = BcraDebtFound(3, Historial(("190012", 6), ("190101", 2), ("190107", 3), ("190011", 4)));

        var r = await Score(sut, BaselineRatios(), bcra, MacroBenign());

        Assert.Equal(1, r.BcraTrendDelta);      // 3 - 2, from the "190101" entry
        Assert.Equal(8.00m, r.BcraTrendAdj);    // min(1*8, 30)
        Assert.Equal(53.00m, r.ScoreBcra);      // 45 + 8
        Assert.Equal(33.55m, r.Score);          // 15.00 + 0.35*53
        Assert.Equal("Low", r.Level);
    }

    // When NO period is at or before the cutoff the Where yields nothing and the `?? historial[^1]`
    // fallback takes the last element BY LIST POSITION, whatever its date. Since the cutoff is now
    // derived from the data, the way to reach that branch is a history SHORTER than the window: this
    // one spans a single month against a six-month window.
    //
    //   anchor = "190102" → cutoff = 1901*12 + (2-1) - 6 = 22807 → year 1900, month 8 → "190008"
    //   nothing at or before "190008" → historial[^1] = ("190102", 2) → delta = 3 - 2 = +1
    //   adj = min(1*8, 30) = 8.00 → scoreBcra = 53.00 → Score = 15.00 + 0.35*53 = 33.55
    //
    // The fixture is deliberately NOT in the most-recent-first order the model documents, so the
    // POSITIONAL nature of the fallback is what the assertion pins: "190102" is the newest period
    // here and it is chosen purely for being last. A date-based fallback would pick "190101" and give
    // delta 3-6 = -3.
    [Fact]
    public async Task Calculate_BcraTrend_FallsBackToLastListEntry_WhenNoPeriodPredatesCutoff()
    {
        var sut = await BuildAsync();

        var bcra = BcraDebtFound(3, Historial(("190101", 6), ("190102", 2)));

        var r = await Score(sut, BaselineRatios(), bcra, MacroBenign());

        Assert.Equal(1, r.BcraTrendDelta);      // 3 - 2, from "190102" = historial[^1] by position
        Assert.Equal(8.00m, r.BcraTrendAdj);
        Assert.Equal(53.00m, r.ScoreBcra);
        Assert.Equal(33.55m, r.Score);
    }

    // The trend block lives inside the DebtFound arm, so NoDebtReported never trends however rich
    // its history is. The source comment says so; nothing asserted it.
    [Fact]
    public async Task Calculate_BcraNoDebtReported_NeverTrends_EvenWithAFullHistory()
    {
        var sut = await BuildAsync();

        var bcra = BcraNoDebt(Historial(("190012", 6), ("190006", 6)));

        var r = await Score(sut, BaselineRatios(), bcra, MacroBenign());

        Assert.Equal(0,  r.BcraTrendDelta);
        Assert.Equal(0m, r.BcraTrendAdj);
        Assert.Equal(0m, r.ScoreBcra);
        Assert.Equal(15.00m, r.Score);
        Assert.False(r.BcraExcluded);
    }

    // BcraService now fills all three history series on NoDebtReported when Deudas/Historicas carries
    // past debt (a CUIT clean today, situación 3-6 months ago). That must leave the score untouched —
    // not just Score and the trend fields, but every field of the result, so the comparison is over the
    // whole serialised ScoringResult. A cheques block rides along on both sides, so the §7.1 penalty
    // runs on this path too and is shown to be blind to the history. Whether past situaciones should
    // weigh on a company with no current debt is a separate product decision, deliberately not taken.
    [Fact]
    public async Task Calculate_BcraNoDebtReported_WithAFullHistory_ScoresIdenticallyToWithout()
    {
        var sut     = await BuildAsync();
        var cheques = ChequesFound(montoRechazado: 1000m, montoImpago: 400m);

        var sinHistoria = BcraNoDebt(cheques: cheques);
        var conHistoria = BcraNoDebt(Historial(("190012", 3), ("190009", 5), ("190006", 6)), cheques);
        conHistoria.Deuda!.HistorialMontos =
        [
            new BcraMontoPeriodo { Periodo = "190012", MontoTotal = 40m },
            new BcraMontoPeriodo { Periodo = "190009", MontoTotal = 90m },
            new BcraMontoPeriodo { Periodo = "190006", MontoTotal = 60m },
        ];
        conHistoria.Deuda.HistorialMontosPorSituacion =
        [
            new BcraMontoSituacionPeriodo { Periodo = "190012", MontoSituacion1 = 10m, MontoSituacion23 = 30m },
            new BcraMontoSituacionPeriodo { Periodo = "190009", MontoSituacion46 = 90m },
            new BcraMontoSituacionPeriodo { Periodo = "190006", MontoSituacion46 = 60m },
        ];

        var a = await Score(sut, BaselineRatios(), sinHistoria, MacroBenign());
        var b = await Score(sut, BaselineRatios(), conHistoria, MacroBenign());

        Assert.Equal(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b));
        Assert.Equal(0,  b.BcraTrendDelta);
        Assert.Equal(0m, b.BcraTrendAdj);
        Assert.NotEqual(0m, b.ChequesAdj);   // the penalty did run, on both sides alike
        Assert.False(b.BcraExcluded);
    }

    // ── 4. Determinism: the cutoff comes from the DATA, not the calendar ────────────────────────
    //
    // This is the regression guard for finding D2. Two histories with IDENTICAL relative spacing but
    // set a thousand years apart must score identically, because the cutoff is derived from each
    // fixture's own newest period. Both are the §3 fixture:
    //
    //   past   ("190012", 6), ("190101", 2), ("190107", 3), ("190011", 4)
    //   future ("290012", 6), ("290101", 2), ("290107", 3), ("290011", 4)
    //
    //   past:   anchor "190107" → cutoff 1901*12 + 6 - 6 = 22812  → "190101" → winner ("190101", 2)
    //   future: anchor "290107" → cutoff 2901*12 + 6 - 6 = 34812  → "290101" → winner ("290101", 2)
    //   both:   delta 3 - 2 = +1, adj min(1*8, 30) = 8.00, scoreBcra 45 + 8 = 53.00,
    //           Score 15.00 + 0.35*53 = 33.55
    //
    // Under the old DateTime.UtcNow anchor these two provably DISAGREED, whatever the run month: the
    // past history sits entirely at or before any realistic clock cutoff (winner would be its own
    // newest, "190107" situación 3 → delta 0 → Score 30.75) while the future history sits entirely
    // above it (nothing selected → historial[^1] = ("290011", 4) → delta -1 → adj -5 → scoreBcra 40 →
    // Score 29.00). So a regression to the clock fails this test rather than rotting quietly.
    //
    // The second half scores ONE payload twice and demands the same answer — the engine holds no
    // state and mutates no input.
    [Fact]
    public async Task Calculate_BcraTrend_IsInvariantUnderTheCalendar_AndUnderRepeatedScoring()
    {
        var sut = await BuildAsync();

        var past   = BcraDebtFound(3, Historial(("190012", 6), ("190101", 2), ("190107", 3), ("190011", 4)));
        var future = BcraDebtFound(3, Historial(("290012", 6), ("290101", 2), ("290107", 3), ("290011", 4)));

        var rPast   = await Score(sut, BaselineRatios(), past,   MacroBenign());
        var rFuture = await Score(sut, BaselineRatios(), future, MacroBenign());

        // Same relative spacing → same comparison period → same everything.
        Assert.Equal(rPast.BcraTrendDelta, rFuture.BcraTrendDelta);
        Assert.Equal(rPast.BcraTrendAdj,   rFuture.BcraTrendAdj);
        Assert.Equal(rPast.ScoreBcra,      rFuture.ScoreBcra);
        Assert.Equal(rPast.Score,          rFuture.Score);

        // Pinned to literals as well, so the test still fails if BOTH sides drift together.
        Assert.Equal(1, rPast.BcraTrendDelta);
        Assert.Equal(8.00m,  rPast.BcraTrendAdj);
        Assert.Equal(53.00m, rPast.ScoreBcra);
        Assert.Equal(33.55m, rPast.Score);

        // Scoring the very same payload again yields the very same result.
        var rAgain = await Score(sut, BaselineRatios(), past, MacroBenign());

        Assert.Equal(rPast.BcraTrendDelta, rAgain.BcraTrendDelta);
        Assert.Equal(rPast.BcraTrendAdj,   rAgain.BcraTrendAdj);
        Assert.Equal(rPast.ScoreBcra,      rAgain.ScoreBcra);
        Assert.Equal(rPast.Score,          rAgain.Score);
        Assert.Equal(rPast.Level,          rAgain.Level);
    }

    // ── 5. A malformed anchor skips the adjustment ──────────────────────────────────────────────
    //
    // Periodo is a plain string copied verbatim off the BCRA payload — BcraService.MapDeuda never
    // parses or normalises it — so a stored blob can carry anything. When the ANCHOR (the ordinal max
    // period) is not a well-formed YYYYMM there is no honest cutoff to derive, and the whole trend
    // adjustment is skipped: delta 0, adj 0, scoreBcra left at the raw situación-3 value of 45.00 and
    // Score at 15.00 + 0.35*45 = 30.75. That is exactly the Count < 2 behaviour. Falling back to the
    // wall clock here would reintroduce finding D2, so the skip is the contract.
    //
    // Every value below is the ordinal max of its own fixture (each sorts above "190001"), so each one
    // really is the anchor rather than an ignored bystander. Only the anchor is parsed — a malformed
    // period elsewhere in the list simply loses the ordinal comparison and is never selected.
    [Theory]
    [InlineData("9901")]        // four characters
    [InlineData("20240101")]    // eight — a date, not a period
    [InlineData("nov-2024")]    // eight, non-digit — the shape legacy hand-entered data takes
    [InlineData("1901AB")]      // six, but the month field is not numeric
    [InlineData("190100")]      // month 00
    [InlineData("190113")]      // month 13
    public async Task Calculate_BcraTrend_WhenNewestPeriodIsMalformed_SkipsTheAdjustment(
        string malformedNewestPeriodo)
    {
        var sut = await BuildAsync();

        var bcra = BcraDebtFound(3, Historial((malformedNewestPeriodo, 6), ("190001", 2)));

        var r = await Score(sut, BaselineRatios(), bcra, MacroBenign());

        Assert.Equal(0,  r.BcraTrendDelta);
        Assert.Equal(0m, r.BcraTrendAdj);
        Assert.Equal(45.00m, r.ScoreBcra);   // unadjusted situación-3 value
        Assert.Equal(30.75m, r.Score);       // 15.00 + 0.35*45
        Assert.False(r.BcraExcluded);        // a bad period is not a BCRA outage
    }

    // The control for the theory above: same fixture SHAPE, same second entry, only the anchor made
    // well-formed. Without this, the zeros above would also be satisfied by a fixture that could never
    // have trended in the first place.
    //
    //   anchor "190107" → cutoff 1901*12 + 6 - 6 = 22812 → "190101"
    //   candidates <= "190101" = {"190001"} → comparison situación 2 → delta 3 - 2 = +1
    //   adj min(1*8, 30) = 8.00 → scoreBcra 45 + 8 = 53.00 → Score 15.00 + 0.35*53 = 33.55
    [Fact]
    public async Task Calculate_BcraTrend_WhenNewestPeriodIsWellFormed_StillAdjusts()
    {
        var sut = await BuildAsync();

        var bcra = BcraDebtFound(3, Historial(("190107", 6), ("190001", 2)));

        var r = await Score(sut, BaselineRatios(), bcra, MacroBenign());

        Assert.Equal(1, r.BcraTrendDelta);
        Assert.Equal(8.00m,  r.BcraTrendAdj);
        Assert.Equal(53.00m, r.ScoreBcra);
        Assert.Equal(33.55m, r.Score);
    }

    // ── 6. The scoring-algorithm version stamp ──────────────────────────────────────────────────
    //
    // Every computed result records which algorithm produced it, so an analysis scored under one
    // engine stays distinguishable from one scored under another — the clock-anchored trend window
    // that "2" replaced, or the rejected-cheques penalty that "5" added. Old persisted rows carry
    // an earlier value, or no key at all (read as version 1), and are never recomputed. Asserted
    // against the literal AND the constant: the literal makes a future bump a deliberate edit here,
    // the constant proves the engine stamps the shared value rather than a stray string.
    //
    // The neutral catch-all result deliberately carries NO version — pinned by
    // ScoringServiceConfigTests.Calculate_WhenConfigCannotBeLoaded_ReturnsTheNeutralResult.
    [Fact]
    public async Task Calculate_StampsTheScoringAlgorithmVersion()
    {
        var sut = await BuildAsync();

        var r = await Score(
            sut, BaselineRatios(),
            BcraDebtFound(3, Historial(("190012", 5), ("190006", 5))), MacroBenign());

        Assert.Equal("11", r.ScoringAlgoVersion);   // v11: the acid-test floor needs a weak current ratio (v10: zero current liabilities at the best band + profitability floor needs an operating loss)
        Assert.Equal(ScoringService.ScoringAlgoVersion, r.ScoringAlgoVersion);
    }
}
