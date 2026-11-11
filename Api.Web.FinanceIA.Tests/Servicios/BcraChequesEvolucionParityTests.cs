// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-27

using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Pins <see cref="BcraChequesEvolucion.Build"/> against the Portal's
/// <c>buildChequesEvolucion</c> (<c>apps/portal/src/lib/bcra-cheques-evolucion.ts</c>), which is the
/// SPEC: that function computed this series in the browser from the full ~2936-row array, and the
/// endpoint stopped shipping that array precisely because the fold moved here. Two implementations
/// of one chart in two repos only stay honest if something fails when they diverge, so these cases
/// are ported from the TypeScript's own test file one for one — including the ones that look
/// pedantic, which are exactly the ones a reimplementation gets wrong.
///
/// <para>
/// Three rules carry most of the risk and each has its own case below:
/// <list type="bullet">
/// <item><c>Pagado</c> decides WHETHER a cheque is settled, <c>FechaPago</c> only WHEN — so a
/// <c>Pagado=false</c> row with a payment date is NOT recovered.</item>
/// <item>A settled cheque whose payment cannot be placed after its rejection —no date, or a date
/// before it— is anchored AT the rejection month, entering and leaving in the same month.</item>
/// <item>The series ends at the later of the last rejection and the last payment, so a recovery
/// after the final rejection is still visible.</item>
/// </list>
/// </para>
///
/// <para>
/// Dates are built with <see cref="DateTimeKind.Unspecified"/>, matching what comes back out of the
/// stored blob: BCRA reports calendar days and <c>AnalysisController.BcraJsonOpts</c> registers no
/// date converter. The port reads <c>.Year</c>/<c>.Month</c> as they stand, so these tests would
/// also catch a timezone conversion sneaking in — a first-of-month row is used deliberately.
/// </para>
/// </summary>
public sealed class BcraChequesEvolucionParityTests
{
    // ── Fixture helpers ───────────────────────────────────────────────────────

    private static DateTime D(int year, int month, int day)
        => new(year, month, day, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>An unpaid rejected cheque — the default shape, mirroring the TS fixture.</summary>
    private static BcraChequeDetalle Cheque(DateTime? fechaRechazo, decimal monto)
        => new()
        {
            Causal       = "SIN FONDOS SUFICIENTES",
            Entidad      = 11,
            NroCheque    = 1,
            FechaRechazo = fechaRechazo,
            Monto        = monto,
            FechaPago    = null,
            Pagado       = false
        };

    /// <summary>A cheque settled on <paramref name="fechaPago"/> — mirrors the BFF deriving Pagado from the date.</summary>
    private static BcraChequeDetalle ChequePagado(DateTime? fechaRechazo, DateTime? fechaPago, decimal monto)
        => new()
        {
            Causal       = "SIN FONDOS SUFICIENTES",
            Entidad      = 11,
            NroCheque    = 1,
            FechaRechazo = fechaRechazo,
            Monto        = monto,
            FechaPago    = fechaPago,
            Pagado       = true
        };

    private static (int Mes, int Cantidad, decimal Rechazado, decimal Recuperado, decimal Saldo) Row(
        ChequeMesEvolucion m)
        => (m.Mes, m.CantidadRechazados, m.MontoRechazado, m.MontoRecuperado, m.SaldoNeto);

    // ── The hand-computed fixture: every field asserted ───────────────────────

    /// <summary>
    /// One fixture exercising five rules at once, with every figure worked out by hand:
    ///
    /// <code>
    ///   R 2025-01-10  100.000  impago                  → ene bar
    ///   R 2025-01-20   50.000  pagado 2025-03-15       → ene bar, mar recupero
    ///   R 2025-04-05   80.000  pagado, NO fechaPago    → abr bar + abr recupero (anchored)
    ///   R 2025-05-10   40.000  pagado 2025-02-01       → may bar + may recupero (clamped forward)
    ///   R null         77.000  impago                  → excluded, counted in sinFechaRechazo
    ///
    ///   mes     cant  rechazado  recuperado   saldo
    ///   202501   2      150.000       0       150.000
    ///   202502   0            0       0       150.000   ← gap month, saldo CARRIED not reset
    ///   202503   0            0    50.000     100.000   ← the line falls
    ///   202504   1       80.000   80.000      100.000   ← anchored: in and out same month
    ///   202505   1       40.000   40.000      100.000   ← clamped: same
    /// </code>
    ///
    /// February is absent from the input and present in the output, which is the continuity rule.
    /// The 2025-02-01 payment on the last row is a first-of-month on purpose: it must clamp to
    /// 202505 by the max() rule, and must not bucket as 202501 via some timezone shift.
    /// </summary>
    [Fact]
    public void Build_HandComputedFixture_MatchesEveryField()
    {
        var result = BcraChequesEvolucion.Build(
        [
            Cheque(D(2025, 1, 10), 100_000m),
            ChequePagado(D(2025, 1, 20), D(2025, 3, 15), 50_000m),
            ChequePagado(D(2025, 4, 5), null, 80_000m),
            ChequePagado(D(2025, 5, 10), D(2025, 2, 1), 40_000m),
            Cheque(null, 77_000m)
        ]);

        Assert.Equal(
        [
            (202501, 2, 150_000m, 0m,      150_000m),
            (202502, 0, 0m,       0m,      150_000m),
            (202503, 0, 0m,       50_000m, 100_000m),
            (202504, 1, 80_000m,  80_000m, 100_000m),
            (202505, 1, 40_000m,  40_000m, 100_000m)
        ], result.Meses.Select(Row));

        // 50.000 + 80.000 + 40.000. The 77.000 row is impago, so it is not recovered money.
        Assert.Equal(170_000m, result.TotalRecuperado);

        // Equal here because every settled cheque had a rejection month to be placed in. The case
        // where these two diverge is pinned separately below.
        Assert.Equal(170_000m, result.RecuperadoEnSerie);

        Assert.Equal(1,       result.SinFechaRechazo.Cantidad);
        Assert.Equal(77_000m, result.SinFechaRechazo.Monto);

        // Only the 80.000 row: an assumed timing. The 40.000 row HAS a fechaPago — a corrupt one,
        // clamped — and must not be reported as missing one.
        Assert.Equal(1,       result.SinFechaPago.Cantidad);
        Assert.Equal(80_000m, result.SinFechaPago.Monto);

        Assert.Equal(100_000m, result.SaldoNetoFinal);
        Assert.Equal(150_000m, result.MaxMontoRechazado);
        Assert.Equal(150_000m, result.MaxSaldoNeto);
    }

    // ── Absent input ──────────────────────────────────────────────────────────

    /// <summary>
    /// Null is expected, not exceptional — the BFF documents <c>detalle</c> as null for legacy
    /// stored data, and the endpoint calls this on every request including those.
    /// </summary>
    [Fact]
    public void Build_WithNoRows_ReturnsTheAllZeroResult()
    {
        foreach (var detalle in new IReadOnlyList<BcraChequeDetalle>?[] { null, [] })
        {
            var result = BcraChequesEvolucion.Build(detalle);

            Assert.Empty(result.Meses);
            Assert.Equal(0m, result.TotalRecuperado);
            Assert.Equal(0m, result.RecuperadoEnSerie);
            Assert.Equal(0,  result.SinFechaRechazo.Cantidad);
            Assert.Equal(0m, result.SinFechaRechazo.Monto);
            Assert.Equal(0,  result.SinFechaPago.Cantidad);
            Assert.Equal(0m, result.SinFechaPago.Monto);
            Assert.Equal(0m, result.SaldoNetoFinal);
            Assert.Equal(0m, result.MaxMontoRechazado);
            Assert.Equal(0m, result.MaxSaldoNeto);
        }
    }

    // ── The bars ──────────────────────────────────────────────────────────────

    [Fact]
    public void Build_BucketsMontoAndCantidadByRejectionMonthOnly()
    {
        var result = BcraChequesEvolucion.Build(
        [
            Cheque(D(2025, 1, 5),  100_000m),
            Cheque(D(2025, 1, 28),  50_000m),
            Cheque(D(2025, 2, 10),  30_000m)
        ]);

        Assert.Equal(
        [
            (202501, 2, 150_000m),
            (202502, 1, 30_000m)
        ], result.Meses.Select(m => (m.Mes, m.CantidadRechazados, m.MontoRechazado)));
    }

    /// <summary>
    /// A paid cheque still counts as rejected in its own month — the payment belongs to a different
    /// month and must not retroactively shrink the bar.
    /// </summary>
    [Fact]
    public void Build_CountsAPaidChequeInItsRejectionMonthAndItsRecoveryInThePaymentMonth()
    {
        var result = BcraChequesEvolucion.Build([ChequePagado(D(2025, 1, 10), D(2025, 3, 20), 80_000m)]);

        Assert.Equal(
        [
            (202501, 80_000m, 0m),
            (202502, 0m,      0m),
            (202503, 0m,      80_000m)
        ], result.Meses.Select(m => (m.Mes, m.MontoRechazado, m.MontoRecuperado)));
    }

    // ── saldoNeto is the LIVE debt ────────────────────────────────────────────

    /// <summary>
    /// The semantics the whole feature turns on: the line FALLS when a cheque is paid. Reverting the
    /// fold to a gross running total makes this fail.
    /// </summary>
    [Fact]
    public void Build_SaldoFallsInThePaymentMonthAndHoldsOtherwise()
    {
        var result = BcraChequesEvolucion.Build([ChequePagado(D(2025, 1, 10), D(2025, 4, 5), 300_000m)]);

        Assert.Equal(
        [
            (202501, 300_000m),   // rejected → debt appears
            (202502, 300_000m),   // holds
            (202503, 300_000m),   // holds
            (202504, 0m)          // paid → debt falls. A gross cumulative line never would.
        ], result.Meses.Select(m => (m.Mes, m.SaldoNeto)));

        Assert.Equal(0m, result.SaldoNetoFinal);
    }

    [Fact]
    public void Build_KeepsANeverPaidChequeContributingIndefinitely()
    {
        var result = BcraChequesEvolucion.Build(
        [
            Cheque(D(2025, 1, 10), 200_000m),
            ChequePagado(D(2025, 2, 10), D(2025, 4, 10), 90_000m)
        ]);

        // ene, feb, mar, abr — in abr only the paid cheque leaves; the impago stays.
        Assert.Equal([200_000m, 290_000m, 290_000m, 200_000m], result.Meses.Select(m => m.SaldoNeto));
        Assert.Equal(200_000m, result.SaldoNetoFinal);
    }

    /// <summary>
    /// The invariant the rendered table is verified against by hand:
    /// <c>SaldoNeto[m] == Σ_{k&lt;=m}(MontoRechazado[k] − MontoRecuperado[k])</c>, and never negative.
    /// </summary>
    [Fact]
    public void Build_HoldsTheSaldoInvariantAcrossEveryMonth()
    {
        var result = BcraChequesEvolucion.Build(
        [
            ChequePagado(D(2024, 11, 2), D(2025, 2, 14), 125_000m),
            Cheque(D(2024, 12, 20), 64_500m),
            ChequePagado(D(2025, 1, 8), D(2025, 1, 30), 12_000m),
            Cheque(D(2025, 3, 3), 900_000m)
        ]);

        var running = 0m;
        foreach (var mes in result.Meses)
        {
            running += mes.MontoRechazado - mes.MontoRecuperado;
            Assert.Equal(running, mes.SaldoNeto);
            Assert.True(mes.SaldoNeto >= 0m, $"saldo went negative at {mes.Mes}");
        }

        Assert.NotEmpty(result.Meses);
    }

    // ── Continuity ────────────────────────────────────────────────────────────

    /// <summary>Gap months must exist, and must CARRY the saldo — so the line does not skip across empty time.</summary>
    [Fact]
    public void Build_FillsGapMonthsWithZeroBarsAndACarriedSaldo()
    {
        var result = BcraChequesEvolucion.Build(
        [
            Cheque(D(2024, 1, 15), 100_000m),
            Cheque(D(2024, 6, 15),  40_000m)
        ]);

        Assert.Equal([202401, 202402, 202403, 202404, 202405, 202406], result.Meses.Select(m => m.Mes));

        foreach (var gap in result.Meses.Skip(1).Take(4))
        {
            Assert.Equal(0,  gap.CantidadRechazados);
            Assert.Equal(0m, gap.MontoRechazado);
            Assert.Equal(0m, gap.MontoRecuperado);
            Assert.Equal(100_000m, gap.SaldoNeto);   // carried forward, not reset to zero
        }
    }

    [Fact]
    public void Build_CrossesAYearBoundaryWithoutSkipping()
    {
        var result = BcraChequesEvolucion.Build(
        [
            Cheque(D(2024, 11, 10), 10_000m),
            Cheque(D(2025, 2, 10),  10_000m)
        ]);

        Assert.Equal([202411, 202412, 202501, 202502], result.Meses.Select(m => m.Mes));
    }

    /// <summary>
    /// Without this a recovery after the last rejection is invisible: the line would stop at its
    /// peak and hide the very finding the chart exists for.
    /// </summary>
    [Fact]
    public void Build_ExtendsTheSeriesToAPaymentAfterTheLastRejection()
    {
        var result = BcraChequesEvolucion.Build([ChequePagado(D(2025, 3, 5), D(2025, 7, 18), 500_000m)]);

        var last = result.Meses[^1];
        Assert.Equal(202507,   last.Mes);
        Assert.Equal(500_000m, last.MontoRecuperado);
        Assert.Equal(0m,       last.SaldoNeto);
    }

    /// <summary>A first-of-month rejection must bucket in its OWN month — the shift bug, server-side.</summary>
    [Fact]
    public void Build_DoesNotRollAFirstOfMonthBackAMonth()
    {
        var result = BcraChequesEvolucion.Build(
        [
            Cheque(D(2025, 1, 1), 10_000m),
            Cheque(D(2025, 3, 1), 10_000m)
        ]);

        Assert.Equal(202501, result.Meses[0].Mes);
        Assert.Equal(202503, result.Meses[^1].Mes);
    }

    // ── Malformed rows are isolated, not fatal ────────────────────────────────

    [Fact]
    public void Build_ExcludesARowWithNoRejectionDateAndReportsIt()
    {
        var result = BcraChequesEvolucion.Build(
        [
            Cheque(D(2025, 1, 10), 100_000m),
            Cheque(null,            77_000m)
        ]);

        Assert.Single(result.Meses);
        Assert.Equal(100_000m, result.Meses[0].MontoRechazado);
        Assert.Equal(1,        result.SinFechaRechazo.Cantidad);
        Assert.Equal(77_000m,  result.SinFechaRechazo.Monto);
    }

    /// <summary>
    /// Settled but untimed: it never counts as live debt (that would contradict its own "Pagado"
    /// badge), and its recovery is anchored at its rejection month so the table keeps adding up
    /// column by column. The assumed timing is disclosed separately.
    /// </summary>
    [Fact]
    public void Build_AnchorsAPaidChequeWithNoPaymentDateAtItsRejectionMonth()
    {
        var result = BcraChequesEvolucion.Build([ChequePagado(D(2025, 1, 10), null, 250_000m)]);

        Assert.Equal(250_000m, result.TotalRecuperado);
        Assert.Equal(1,        result.SinFechaPago.Cantidad);
        Assert.Equal(250_000m, result.SinFechaPago.Monto);

        var only = Assert.Single(result.Meses);
        Assert.Equal((202501, 1, 250_000m, 250_000m, 0m), Row(only));
        Assert.Equal(0m, result.SaldoNetoFinal);
    }

    /// <summary>The disclosure must not fire on a normally dated recovery, or it would appear on every healthy card.</summary>
    [Fact]
    public void Build_DoesNotFlagSinFechaPagoForANormallyDatedRecovery()
    {
        var result = BcraChequesEvolucion.Build([ChequePagado(D(2025, 1, 10), D(2025, 2, 10), 100_000m)]);

        Assert.Equal(0,  result.SinFechaPago.Cantidad);
        Assert.Equal(0m, result.SinFechaPago.Monto);
        Assert.Equal(result.TotalRecuperado, result.RecuperadoEnSerie);
    }

    /// <summary>
    /// A settled cheque with NEITHER date cannot be placed at all — no rejection month to anchor to,
    /// so it leaves the series entirely and only the total remembers it. This is the one case where
    /// <c>RecuperadoEnSerie</c> and <c>TotalRecuperado</c> diverge, and it also pins the ORDER of the
    /// two checks: the undated row exits before the payment block, so it is not counted in
    /// <c>SinFechaPago</c> as well.
    /// </summary>
    [Fact]
    public void Build_DropsASettledChequeWithNeitherDateFromTheSeriesButKeepsItInTheTotal()
    {
        var result = BcraChequesEvolucion.Build([ChequePagado(null, null, 70_000m)]);

        Assert.Equal(70_000m, result.TotalRecuperado);
        Assert.Equal(0m,      result.RecuperadoEnSerie);
        Assert.Equal(1,       result.SinFechaRechazo.Cantidad);
        Assert.Equal(70_000m, result.SinFechaRechazo.Monto);
        Assert.Equal(0,       result.SinFechaPago.Cantidad);
        Assert.Empty(result.Meses);
    }

    /// <summary>
    /// <c>Pagado</c> is authoritative for WHETHER. A false flag beside a payment date is legacy or
    /// hand-written data, and the flag wins — re-deriving settlement from the date here would
    /// silently recover money the row says is still owed.
    /// </summary>
    [Fact]
    public void Build_TrustsPagadoFalseEvenWhenAPaymentDateIsPresent()
    {
        var row = Cheque(D(2025, 1, 10), 60_000m);
        row.FechaPago = D(2025, 2, 10);   // present, but Pagado stays false

        var result = BcraChequesEvolucion.Build([row]);

        Assert.Equal(0m, result.TotalRecuperado);
        Assert.All(result.Meses, m => Assert.Equal(0m, m.MontoRecuperado));
        Assert.Equal(60_000m, result.SaldoNetoFinal);
        Assert.Equal(60_000m, result.Meses[^1].SaldoNeto);
    }

    /// <summary>
    /// A payment dated before its own rejection is corrupt. Anchoring it forward keeps the series
    /// from being dragged backwards into a meaningless negative live debt.
    /// </summary>
    [Fact]
    public void Build_ClampsAPaymentDatedBeforeItsRejectionIntoTheRejectionMonth()
    {
        var result = BcraChequesEvolucion.Build([ChequePagado(D(2025, 5, 10), D(2025, 1, 2), 40_000m)]);

        var only = Assert.Single(result.Meses);
        Assert.Equal((202505, 1, 40_000m, 40_000m, 0m), Row(only));
        Assert.All(result.Meses, m => Assert.True(m.SaldoNeto >= 0m));
    }

    // ── Degenerate and adversarial shapes ─────────────────────────────────────

    [Fact]
    public void Build_RendersASingleMonthDetalleAsExactlyOneRow()
    {
        var result = BcraChequesEvolucion.Build(
        [
            Cheque(D(2024, 11, 5),  100_000m),
            Cheque(D(2024, 11, 25),  20_000m)
        ]);

        var only = Assert.Single(result.Meses);
        Assert.Equal((202411, 2, 120_000m, 0m, 120_000m), Row(only));
        Assert.Equal(120_000m, result.SaldoNetoFinal);
    }

    /// <summary>
    /// The stored <c>detalle</c> arrives <c>fechaRechazo</c> DESCENDING, but the fold is into keyed
    /// dictionaries and the output is walked start→end, so nothing about the result may depend on
    /// input order. Compared as serialised JSON, which catches every field including ones a future
    /// addition might forget to assert.
    /// </summary>
    [Fact]
    public void Build_ProducesAnIdenticalResultRegardlessOfInputOrder()
    {
        List<BcraChequeDetalle> rows =
        [
            Cheque(D(2025, 3, 1), 30_000m),
            ChequePagado(D(2025, 1, 1), D(2025, 4, 15), 10_000m),
            Cheque(D(2025, 2, 1), 20_000m),
            ChequePagado(D(2025, 2, 20), null, 5_000m),
            Cheque(null, 9_000m)
        ];

        var expected = JsonSerializer.Serialize(BcraChequesEvolucion.Build(rows));

        // Fixed permutations, not a seeded RNG: a shuffle that varies between runs would make a
        // failure here unreproducible.
        int[][] permutations =
        [
            [4, 3, 2, 1, 0],
            [2, 0, 4, 1, 3],
            [1, 4, 0, 3, 2],
            [3, 2, 1, 0, 4]
        ];

        foreach (var order in permutations)
        {
            var shuffled = order.Select(i => rows[i]).ToList();
            Assert.Equal(expected, JsonSerializer.Serialize(BcraChequesEvolucion.Build(shuffled)));
        }

        // And the series is ascending in every case, not merely consistent.
        Assert.Equal([202501, 202502, 202503, 202504], BcraChequesEvolucion.Build(rows).Meses.Select(m => m.Mes));
    }

    /// <summary>
    /// A fully recovered series must end on EXACTLY zero, so a table formatting "$ 0" cannot sit
    /// beside a chart drawing the line off the axis. In <c>decimal</c> these sums are exact, so this
    /// passes on arithmetic alone — which is the point: the port must not reintroduce <c>double</c>,
    /// and this fails if it ever does.
    /// </summary>
    [Fact]
    public void Build_LandsExactlyOnZeroWhenEverythingIsRecovered()
    {
        var result = BcraChequesEvolucion.Build(
        [
            ChequePagado(D(2025, 1, 5), D(2025, 2, 5), 150_000.5m),
            ChequePagado(D(2025, 1, 6), D(2025, 2, 6),  80_000.1m),
            ChequePagado(D(2025, 1, 7), D(2025, 2, 7),  25_000.2m)
        ]);

        Assert.Equal(0m, result.SaldoNetoFinal);
        Assert.Equal(0m, result.Meses[^1].SaldoNeto);
        Assert.Equal(255_000.8m, result.TotalRecuperado);
    }

    /// <summary>
    /// The centavo epsilon itself. Unreachable via floating-point residue here —<c>decimal</c> sums
    /// exactly— but still live for a genuinely sub-centavo saldo, which it snaps to zero rather than
    /// drawing a line a hair above the axis for a tenth of a centavo of debt.
    /// </summary>
    [Fact]
    public void Build_SnapsASubCentavoSaldoToZero()
    {
        var result = BcraChequesEvolucion.Build([Cheque(D(2025, 1, 10), 0.001m)]);

        var only = Assert.Single(result.Meses);
        Assert.Equal(0.001m, only.MontoRechazado);   // the bar keeps the real figure
        Assert.Equal(0m,     only.SaldoNeto);        // the line snaps
        Assert.Equal(0m,     result.SaldoNetoFinal);
        Assert.Equal(0m,     result.MaxSaldoNeto);
    }

    [Fact]
    public void Build_ReportsAxisMaximaIncludingWhenTheLinePeaksAfterTheBar()
    {
        var result = BcraChequesEvolucion.Build(
        [
            ChequePagado(D(2025, 1, 5), D(2025, 1, 20), 90_000m),
            Cheque(D(2025, 2, 5), 40_000m)
        ]);

        Assert.Equal(90_000m, result.MaxMontoRechazado);   // ene bar
        Assert.Equal(40_000m, result.MaxSaldoNeto);        // ene closes at 0; feb carries 40k
        Assert.Equal(40_000m, result.SaldoNetoFinal);
    }

    /// <summary>Zero maxima on an all-zero set, so an axis scale can guard against dividing by them.</summary>
    [Fact]
    public void Build_ReportsZeroMaximaWhenEveryMontoIsZero()
    {
        var result = BcraChequesEvolucion.Build([Cheque(D(2025, 1, 5), 0m)]);

        Assert.Single(result.Meses);
        Assert.Equal(0m, result.MaxMontoRechazado);
        Assert.Equal(0m, result.MaxSaldoNeto);
    }

    /// <summary>
    /// The backstop: a corrupt pair of dates centuries apart must not walk 2.400 months into a
    /// response. Capped at 600 (50 years), still starting at the first rejection.
    /// </summary>
    [Fact]
    public void Build_CapsTheWalkInsteadOfAllocatingAnUnboundedSeries()
    {
        var result = BcraChequesEvolucion.Build(
        [
            Cheque(D(1825, 1, 10), 1_000m),
            Cheque(D(2025, 1, 10), 1_000m)
        ]);

        Assert.Equal(600,    result.Meses.Count);
        Assert.Equal(182501, result.Meses[0].Mes);

        // The truncated tail never contributed: SaldoNetoFinal is the LAST EMITTED month's saldo,
        // not the true end of the series — exactly as the TypeScript reports it.
        Assert.Equal(1_000m, result.SaldoNetoFinal);
    }
}
