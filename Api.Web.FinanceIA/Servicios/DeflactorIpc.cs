// Project : Api.Web.FinanceIA
// Created : 2026-09-24

using System.Globalization;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Period arithmetic and CPI deflation shared by the scoring engine and the PDF export.
/// <para>
/// Every member was MOVED here verbatim from <see cref="ScoringService"/>, where it was private, so the
/// PDF's "Ventas declaradas (LUFE)" section can deflate with exactly the arithmetic the score uses
/// instead of a second copy that could drift from it. Nothing in the bodies changed: the scoring result,
/// and therefore <see cref="ScoringService.ScoringAlgoVersion"/>, is unaffected. Change a body here and
/// you change the score — treat it as scoring code.
/// </para>
/// <para>
/// Pure and static: no clock, no culture beyond <see cref="CultureInfo.InvariantCulture"/>, no state.
/// </para>
/// </summary>
internal static class DeflactorIpc
{
    // ── Period arithmetic ─────────────────────────────────────────────────────

    /// <summary>
    /// Shifts a BCRA "YYYYMM" period by <paramref name="months"/> (negative shifts backwards) and
    /// returns the result in the same six-character form, so it stays directly comparable with
    /// <see cref="StringComparer.Ordinal"/> against the periods in the history.
    /// <para>
    /// Returns <c>null</c> — never a clock-derived value — when <paramref name="periodo"/> is not a
    /// well-formed period (null, not six characters, non-digit, or a month outside 01-12) or when the
    /// shift would leave the four-digit-year range. The caller then skips the trend adjustment
    /// entirely rather than comparing against a fabricated cutoff.
    /// </para>
    /// <para>
    /// The format is a convention, not an invariant: <c>BcraService.MapDeuda</c> copies
    /// <c>Periodo</c> verbatim off the BCRA payload without parsing or normalising it, so anything
    /// can reach here from a stored blob.
    /// </para>
    /// </summary>
    internal static string? ShiftPeriodoMonths(string? periodo, int months)
    {
        if (periodo is not { Length: 6 }) return null;

        // NumberStyles.None rejects signs, whitespace and group separators, so "+1901" and " 1901"
        // cannot pass for a year.
        if (!int.TryParse(periodo.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) ||
            !int.TryParse(periodo.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var month) ||
            month is < 1 or > 12)
        {
            return null;
        }

        // Arithmetic on an absolute month index — no DateTime, so no calendar and no culture is
        // involved in producing the cutoff.
        var shifted = (year * 12) + (month - 1) + months;
        if (shifted < 0) return null;

        var shiftedYear  = shifted / 12;
        var shiftedMonth = (shifted % 12) + 1;

        // A year outside 1-9999 would format to something other than four digits and break the
        // ordinal comparison the caller relies on.
        if (shiftedYear is < 1 or > 9999) return null;

        return string.Create(CultureInfo.InvariantCulture, $"{shiftedYear:D4}{shiftedMonth:D2}");
    }

    // ── CPI deflation ─────────────────────────────────────────────────────────

    /// <summary>One month of the three-month basis and the same calendar month twelve earlier.</summary>
    internal readonly record struct ParInteranual(string Mes, decimal Ventas, string MesPrevio, decimal VentasPrevio);

    /// <summary>
    /// The REAL variation of the three-month basis with each pair deflated by its own twelve-month factor,
    /// or <c>null</c> when the CPI series cannot cover all three pairs — the caller then falls back to one
    /// deflator over the endpoint span.
    /// <para>
    /// Each recent month is restated in the prices of its own year-earlier month — divided by the inflation
    /// accumulated between the two, <see cref="AcumularInflacion"/>(t−12, t) — and only then summed against
    /// the base quarter: Σ ventasₜ / Dₜ against Σ ventasₜ₋₁₂. That is the base-weighted mean of the three
    /// pairwise real variations, so each pair is compared at exactly the prices that separate it. It needs
    /// CPI readings from ultimo−13 to ultimo (the oldest pair, ultimo−14 → ultimo−2, reads from ultimo−13),
    /// two months more than the endpoint span's ultimo−11 to ultimo. The 26-month window <c>BcraService</c> fetches
    /// normally covers both; when it reaches back only far enough for the endpoint span, the caller's
    /// single-deflator fallback applies.
    /// </para>
    /// <para>
    /// A pair that cannot be restated — a hole in its window — refuses the whole per-pair reading rather
    /// than mixing deflated and nominal months in one sum. An overflow restating corrupt data (a factor
    /// near zero) does the same, so the fallback still gets its chance instead of the catch-all's neutral 50.
    /// </para>
    /// <para>
    /// Scoring always passes the three pairs of the interanual_3m basis. The PDF's LUFE section passes up
    /// to twelve — every month of its window whose year-earlier month is also declared — and the method
    /// is the same weighted mean for any count; nothing in it depends on there being three.
    /// </para>
    /// </summary>
    internal static decimal? VariacionRealPorPares(
        IReadOnlyList<ParInteranual>         pares,
        IReadOnlyDictionary<string, double>? ipcMensual)
    {
        decimal recienteReal = 0m;
        decimal previo       = 0m;

        try
        {
            foreach (var par in pares)
            {
                if (AcumularInflacion(ipcMensual, par.MesPrevio, par.Mes) is not { } deflactor || deflactor <= 0m)
                    return null;

                recienteReal += par.Ventas / deflactor;
                previo       += par.VentasPrevio;
            }
        }
        catch (OverflowException)
        {
            return null;
        }

        if (previo <= 0m) return null;

        return (recienteReal - previo) / previo;
    }

    /// <summary>
    /// The compounded price factor between two "YYYYMM" months — the product of
    /// <c>(1 + iₘ/100)</c> over the months AFTER <paramref name="desde"/> up to and including
    /// <paramref name="hasta"/>, which is exactly the set of monthly readings that separate the two
    /// price levels. Returns <c>null</c> whenever the span is not fully and usably covered, which the
    /// caller reads as "measure this nominally".
    /// <para>
    /// Guaranteed strictly positive when non-null, so the caller can divide by it unconditionally.
    /// </para>
    /// <para>
    /// A month whose stored value is exactly ZERO counts as a hole, not as a month without inflation.
    /// <c>BcraMonetariasDetalleNode.Valor</c> is a non-nullable <c>double</c>, so a wire point missing
    /// its <c>valor</c> deserialises to 0.0 and would otherwise compound as "prices did not move",
    /// turning absent data into a confidently wrong real figure. The cost is that a genuine 0.00 %
    /// month would also be read as a hole and send the basis to nominal; the modern Argentine series
    /// has never printed one, and a reported nominal reading is the safe side of that trade.
    /// </para>
    /// <para>
    /// Iteration is bounded by the size of the series itself: every month of the span must be a key
    /// in it, so a span longer than the dictionary cannot possibly be covered and the walk stops.
    /// The accumulated factor is bounded too — <c>decimal</c> multiplication THROWS on overflow, and
    /// <c>CalculateAsync</c>'s catch-all would turn that into a neutral 50, discarding a real score
    /// over a corrupt macro payload.
    /// </para>
    /// </summary>
    internal static decimal? AcumularInflacion(
        IReadOnlyDictionary<string, double>? ipcMensual,
        string                               desde,
        string                               hasta)
    {
        if (ipcMensual is null || ipcMensual.Count == 0) return null;

        // A price factor this large is not a reading, it is corrupt data — and stopping here is what
        // keeps the multiplication below away from decimal's range.
        const decimal FactorAcumuladoMaximo = 1_000_000m;

        decimal acumulado = 1m;

        for (int k = 1; k <= ipcMensual.Count; k++)
        {
            if (ShiftPeriodoMonths(desde, k) is not { } periodo) return null;
            if (string.CompareOrdinal(periodo, hasta) > 0)       return null;   // desde was not before hasta

            if (!ipcMensual.TryGetValue(periodo, out var mensual)) return null;

            // Zero is a hole, per the remarks above. The range test is what makes the cast below
            // safe: a double outside decimal's range throws on conversion, and NaN and ±Infinity
            // fail both comparisons, so all three are rejected by the one pattern.
            if (mensual == 0d || mensual is not (>= -1_000_000d and <= 1_000_000d)) return null;

            decimal factor = 1m + ((decimal)mensual / 100m);
            if (factor <= 0m) return null;   // a reading at or below -100 % is not a price level

            acumulado *= factor;
            if (acumulado > FactorAcumuladoMaximo) return null;

            if (string.CompareOrdinal(periodo, hasta) == 0) return acumulado;
        }

        // Ran out of months before reaching `hasta`: the span is longer than the series can cover.
        return null;
    }
}
