// Project : Api.Web.FinanceIA
// Created : 2026-08-27

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One month bucket in the rejected-cheque evolution series.
///
/// <para>
/// The pair the chart is built from: <see cref="MontoRechazado"/> is the BAR (what was rejected
/// this month) and <see cref="SaldoNeto"/> is the LINE (the "deuda viva" still outstanding at the
/// end of it). The line RISES on a rejection and FALLS when a cheque is later paid, which is the
/// whole point — a gross running total never falls, so it could never answer "¿recuperó algo?".
/// </para>
/// </summary>
public sealed class ChequeMesEvolucion
{
    /// <summary>
    /// YYYYMM as a NUMBER (202503). Monotonic, so <c>&lt;</c> / <c>&gt;</c> are calendar order and
    /// the series needs no date type to stay sorted.
    /// </summary>
    public int Mes { get; set; }

    /// <summary>Cheques REJECTED in this month.</summary>
    public int CantidadRechazados { get; set; }

    /// <summary>Σ monto rejected in this month — the bar height.</summary>
    public decimal MontoRechazado { get; set; }

    /// <summary>Σ monto of cheques PAID in this month.</summary>
    public decimal MontoRecuperado { get; set; }

    /// <summary>Cumulative live debt at the END of this month — the line. Always &gt;= 0.</summary>
    public decimal SaldoNeto { get; set; }
}

/// <summary>
/// A count-and-amount pair for rows the series could not place on the time axis. Disclosed rather
/// than silently dropped: the money is real, only its position is unknown.
/// </summary>
public sealed class ChequesSinFecha
{
    public int Cantidad { get; set; }

    public decimal Monto { get; set; }
}

/// <summary>
/// The monthly evolution of an analysis's rejected cheques, folded from
/// <c>BcraChequesData.Detalle</c> at read time by
/// <see cref="Servicios.BcraChequesEvolucion.Build"/>.
///
/// <para>
/// This is the ~24-point summary that replaced shipping the ~2936-row array on
/// GET /api/analyses/{id}/bcra/cheques. It is a rule-for-rule port of the Portal's
/// <c>buildChequesEvolucion</c> (apps/portal/src/lib/bcra-cheques-evolucion.ts), which used to
/// compute exactly this in the browser from the full array — the fold simply moved to the side of
/// the wire that already holds the data.
/// </para>
///
/// <para>
/// Field-for-field with that TypeScript interface, deliberately: the two implementations are pinned
/// against each other by <c>BcraChequesEvolucionParityTests</c>, and a renamed field here would
/// make that pinning silently vacuous.
/// </para>
/// </summary>
public sealed class ChequesEvolucion
{
    /// <summary>
    /// Continuous (gap months filled), ascending by <see cref="ChequeMesEvolucion.Mes"/>. Empty when
    /// no row carries a usable <c>fechaRechazo</c>.
    /// </summary>
    public List<ChequeMesEvolucion> Meses { get; set; } = [];

    /// <summary>
    /// Σ monto where <c>pagado</c> is true, over EVERY row — including rows the series could not
    /// place. This is the honest "recuperado" figure.
    /// </summary>
    public decimal TotalRecuperado { get; set; }

    /// <summary>
    /// Σ <see cref="ChequeMesEvolucion.MontoRecuperado"/> across <see cref="Meses"/>. Lower than
    /// <see cref="TotalRecuperado"/> when a paid cheque had no rejection month to anchor to.
    /// </summary>
    public decimal RecuperadoEnSerie { get; set; }

    /// <summary>Rows excluded from the series because <c>fechaRechazo</c> is missing.</summary>
    public ChequesSinFecha SinFechaRechazo { get; set; } = new();

    /// <summary>
    /// Settled cheques reporting no <c>fechaPago</c>, whose recovery was therefore anchored at their
    /// own rejection month rather than a known one. Surfaced so the caller can disclose it — the
    /// amount is real, only its timing is assumed.
    /// </summary>
    public ChequesSinFecha SinFechaPago { get; set; } = new();

    /// <summary>Live debt at the end of the series. 0 when empty.</summary>
    public decimal SaldoNetoFinal { get; set; }

    /// <summary>Bar-axis maximum. 0 when empty.</summary>
    public decimal MaxMontoRechazado { get; set; }

    /// <summary>Line-axis maximum. 0 when empty.</summary>
    public decimal MaxSaldoNeto { get; set; }
}

/// <summary>
/// The rejection-date range of a cheque set — what a date filter needs in order to bound its own
/// inputs without holding the rows.
///
/// <para>
/// Always present on the response; both fields are null when no row carries a
/// <c>fechaRechazo</c>. Taken over the rejection date only: <c>fechaPago</c> can fall outside this
/// range, and does whenever a cheque was settled after the last rejection.
/// </para>
/// </summary>
public sealed class ChequeDateBounds
{
    /// <summary>Earliest <c>fechaRechazo</c> across the rows, or null when none is dated.</summary>
    public DateTime? Min { get; set; }

    /// <summary>Latest <c>fechaRechazo</c> across the rows, or null when none is dated.</summary>
    public DateTime? Max { get; set; }
}
