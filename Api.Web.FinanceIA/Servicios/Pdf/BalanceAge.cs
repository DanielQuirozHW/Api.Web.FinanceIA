// Project : Api.Web.FinanceIA
// Created : 2026-11-09

using System.Globalization;

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// How old a balance is, as page 1's "Vigencia de los datos" says it: complete months counted from the LAST DAY of the
/// closing month (a balance closed 11/2025 is 11 months old on 08/11/2026, 12 on 30/11/2026). Pure; the caller passes
/// the Argentine local date.
/// </summary>
internal static class BalanceAge
{
    /// <summary>The parenthetical after "cierre MM/AAAA", with its leading space; empty for a closing in the future.</summary>
    internal static string Suffix(int closingYear, int closingMonth, DateTime nowLocal)
    {
        int monthDiff = ((nowLocal.Year * 12) + nowLocal.Month) - ((closingYear * 12) + closingMonth);
        if (monthDiff < 0)
            return string.Empty;
        if (monthDiff == 0)
            return " (cierre en el mes en curso)";

        int elapsed = CompleteMonths(closingYear, closingMonth, nowLocal);
        return elapsed switch
        {
            0 => " (hace menos de un mes)",
            1 => " (hace 1 mes)",
            _ => $" (hace {elapsed.ToString(CultureInfo.InvariantCulture)} meses)",
        };
    }

    /// <summary>
    /// Complete months since the closing month's last day: the calendar difference, less one while today's day has
    /// not reached that last day — capped at this month's length, so a 31st closing turns a month on the 30th.
    /// </summary>
    internal static int CompleteMonths(int closingYear, int closingMonth, DateTime nowLocal)
    {
        int monthDiff = ((nowLocal.Year * 12) + nowLocal.Month) - ((closingYear * 12) + closingMonth);
        int anniversaryDay = Math.Min(
            DateTime.DaysInMonth(closingYear, closingMonth),
            DateTime.DaysInMonth(nowLocal.Year, nowLocal.Month));

        return nowLocal.Day < anniversaryDay ? monthDiff - 1 : monthDiff;
    }
}
