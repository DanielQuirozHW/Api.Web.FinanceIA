// Project : Api.Web.FinanceIA
// Created : 2026-10-25

using Api.Web.FinanceIA.Clases.Time;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// The maturity bands of a stored-'Vigente' operation, in one place: the portfolio dashboard buckets its
/// maturities with them and <see cref="CupoAlertService"/> raises CUPO_OPERATIONS_EXPIRING on the first one.
/// <para>
/// Extracted from <c>DashboardService.Portfolio</c>, which owned them as private members while the dashboard
/// was their only reader — the reasoning that produced <see cref="CreditLineRules"/>: an alert that counted
/// "por vencer" one way and a dashboard band that counted it another would disagree on the same cupo.
/// </para>
/// </summary>
internal static class MaturityBands
{
    /// <summary>Due within 0..30 Argentine calendar days.</summary>
    public const string Days0To30 = "Days0To30";

    /// <summary>Due within 31..60 Argentine calendar days.</summary>
    public const string Days31To60 = "Days31To60";

    /// <summary>Due 61 or more Argentine calendar days away.</summary>
    public const string Days61To90 = "Days61To90";

    /// <summary>The upper bound, in Argentine days, of <see cref="Days0To30"/>.</summary>
    public const int FirstBandDays = 30;

    /// <summary>The Argentine calendar date of <paramref name="nowUtc"/>, the day the bands count from.</summary>
    public static DateOnly AsOf(DateTime nowUtc) => DateOnly.FromDateTime(ArgentineTime.ToLocal(nowUtc));

    /// <summary>
    /// The maturity bucket of a stored-'Vigente' operation due on <paramref name="validUntil"/>:
    /// <see cref="CreditLineRules.Vencida"/> when CreditLineRules.EffectiveStatus says so, otherwise the
    /// band of Argentine days between <paramref name="asOf"/> and its date.
    /// </summary>
    public static string BucketOf(DateTime validUntil, DateOnly asOf, DateTime nowUtc)
    {
        var operation = new CreditLineOperation { Status = CreditLineRules.Vigente, ValidUntil = validUntil };
        if (CreditLineRules.EffectiveStatus(operation, nowUtc) == CreditLineRules.Vencida)
            return CreditLineRules.Vencida;

        var days = DateOnly.FromDateTime(validUntil).DayNumber - asOf.DayNumber;
        return days <= FirstBandDays ? Days0To30
             : days <= 60            ? Days31To60
             :                         Days61To90;
    }
}
