// Project : Api.Web.FinanceIA
// Created : 2026-10-30

using System.Globalization;
using Api.Web.FinanceIA.Clases.Time;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// The window of the calibration board: whole ARGENTINE days, inclusive at both ends, as a half-open UTC
/// window over Analyses.CreatedAt.
/// </summary>
/// <param name="From">First Argentine day, inclusive.</param>
/// <param name="To">Last Argentine day, inclusive.</param>
/// <param name="StartUtc">00:00 ART on <paramref name="From"/>, in UTC. INCLUSIVE.</param>
/// <param name="EndUtc">00:00 ART on the day after <paramref name="To"/>, in UTC. EXCLUSIVE.</param>
/// <remarks>
/// The boundary is computed in Argentine time and converted to UTC through <see cref="ArgentineTime.Zone"/>,
/// the same rule ArgentineTime applies to months: an analysis created at 23:30 ART on <c>to</c> is 02:30 UTC
/// the next day and still belongs to the window; one at 00:10 ART the day after does not.
/// </remarks>
public readonly record struct PersonCalibrationPeriod(DateOnly From, DateOnly To, DateTime StartUtc, DateTime EndUtc)
{
    /// <summary>The longest window, in days counted inclusively.</summary>
    public const int MaxDays = 366;

    /// <summary>The default window, in days counted inclusively, ending today in Argentina.</summary>
    public const int DefaultDays = 30;

    /// <summary>The query format of <c>from</c> and <c>to</c>.</summary>
    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>
    /// Resolves the query's <c>from</c> and <c>to</c>. Absent <c>to</c> is today in Argentina; absent
    /// <c>from</c> is <see cref="DefaultDays"/> days ending at <c>to</c>.
    /// </summary>
    /// <returns>The period, or the offending field and an es-AR message.</returns>
    public static (PersonCalibrationPeriod? Period, string? Field, string? Error) Resolve(
        string? from, string? to, DateTime nowUtc)
    {
        DateOnly toDate;
        if (string.IsNullOrWhiteSpace(to))
        {
            toDate = DateOnly.FromDateTime(ArgentineTime.ToLocal(nowUtc));
        }
        else if (!TryParse(to, out toDate))
        {
            return (null, "to", "La fecha 'to' debe tener el formato AAAA-MM-DD.");
        }

        DateOnly fromDate;
        if (string.IsNullOrWhiteSpace(from))
        {
            fromDate = toDate.AddDays(-(DefaultDays - 1));
        }
        else if (!TryParse(from, out fromDate))
        {
            return (null, "from", "La fecha 'from' debe tener el formato AAAA-MM-DD.");
        }

        if (fromDate > toDate)
            return (null, "from", "La fecha 'from' no puede ser posterior a 'to'.");

        if (toDate.DayNumber - fromDate.DayNumber + 1 > MaxDays)
            return (null, "to", $"El período no puede superar los {MaxDays} días.");

        return (new PersonCalibrationPeriod(fromDate, toDate, ToUtc(fromDate), ToUtc(toDate.AddDays(1))), null, null);
    }

    private static bool TryParse(string value, out DateOnly date)
        => DateOnly.TryParseExact(value.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    // Unspecified, NOT Utc: a local wall-clock reading not yet placed on the timeline — the same care
    // ArgentineTime.PeriodFor takes. Midnight is never invalid or ambiguous under a zone without DST.
    private static DateTime ToUtc(DateOnly localDay)
        => TimeZoneInfo.ConvertTimeToUtc(localDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), ArgentineTime.Zone);
}
