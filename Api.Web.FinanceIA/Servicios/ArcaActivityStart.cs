// Project : Api.Web.FinanceIA
// Created : 2026-10-21

using Api.Web.FinanceIA.Models.Arca;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// The one rule for "when did this person's activity start, according to ARCA": the earliest
/// registration period ARCA reports. Used by the legajo writes (PersonLegajo.ActivityStartDate) and by
/// the persona humana evaluation's fallback for a legajo that has no date yet, so both read the same.
/// </summary>
/// <remarks>
/// PROVISIONAL. getPersona_v2 carries no inscription date, only a <c>periodo</c> (YYYYMM) per actividad,
/// impuesto and monotributo category; the earliest of them is the closest ARCA gets. Regímenes are not
/// counted (they are retention regimes, not the activity). It is an APPROXIMATION: ARCA re-dates periods
/// (F.150 updates, nomenclator migrations), so the real start is at or before this date, never after —
/// which is why the evaluation never rejects on it alone.
/// </remarks>
public static class ArcaActivityStart
{
    /// <summary>
    /// The earliest of <see cref="ArcaFiscalData.FechaInicioActividad"/> (computed by the parser across
    /// both sections) and the periods of the snapshot's impuestos and actividades (what an older snapshot,
    /// or an echo without that field, still has). Null when there is no snapshot or no parsable period.
    /// </summary>
    public static DateTime? From(ArcaFiscalData? fiscal)
    {
        if (fiscal is null)
            return null;

        var fromLists = EarliestPeriod(
            (fiscal.Impuestos ?? []).Select(i => i.Periodo)
                .Concat((fiscal.Actividades ?? []).Select(a => a.Periodo)));

        return (fiscal.FechaInicioActividad?.Date, fromLists) switch
        {
            (null, null)       => null,
            ({ } f, null)      => f,
            (null, { } l)      => l,
            ({ } f, { } l)     => f < l ? f : l
        };
    }

    /// <summary>The first day of the earliest parsable YYYYMM period, or null.</summary>
    public static DateTime? EarliestPeriod(IEnumerable<string?> periods)
    {
        ArgumentNullException.ThrowIfNull(periods);

        var parsed = periods
            .Select(PersonEvaluationService.ParseYearMonth)
            .OfType<DateOnly>()
            .ToList();

        return parsed.Count > 0 ? parsed.Min().ToDateTime(TimeOnly.MinValue) : null;
    }
}
