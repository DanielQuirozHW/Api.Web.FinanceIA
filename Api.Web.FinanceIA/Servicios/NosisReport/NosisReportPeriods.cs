// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using System.Globalization;
using System.Text.RegularExpressions;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// Months and periods as the report prints them ("Octubre", "Oct", "Set", "09/2026", "2026-09",
/// "Sep-26") → "YYYY-MM", the one period format of the contract.
/// </summary>
internal static class NosisReportPeriods
{
    private static readonly Dictionary<string, int> Months = new(StringComparer.Ordinal)
    {
        ["ENERO"] = 1, ["ENE"] = 1,
        ["FEBRERO"] = 2, ["FEB"] = 2,
        ["MARZO"] = 3, ["MAR"] = 3,
        ["ABRIL"] = 4, ["ABR"] = 4,
        ["MAYO"] = 5, ["MAY"] = 5,
        ["JUNIO"] = 6, ["JUN"] = 6,
        ["JULIO"] = 7, ["JUL"] = 7,
        ["AGOSTO"] = 8, ["AGO"] = 8,
        ["SEPTIEMBRE"] = 9, ["SETIEMBRE"] = 9, ["SEP"] = 9, ["SET"] = 9, ["SEPT"] = 9,
        ["OCTUBRE"] = 10, ["OCT"] = 10,
        ["NOVIEMBRE"] = 11, ["NOV"] = 11,
        ["DICIEMBRE"] = 12, ["DIC"] = 12,
    };

    private static readonly Regex NumericPeriod = new(
        @"^(?:(\d{1,2})(?:[/\-](\d{4}|\d{2})|\.(\d{4}))|(\d{4})[/\-](\d{1,2}))$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    private static readonly Regex NamedPeriod = new(
        @"^([A-Z]+)\.?[\s/\-]?(\d{4}|\d{2})$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    /// <summary>The month number of a Spanish month name or abbreviation ("Oct", "Octubre", "Set."), or null.</summary>
    public static int? Month(string? token)
    {
        var key = NosisReportText.Normalise(token).TrimEnd('.');
        return Months.TryGetValue(key, out var month) ? month : null;
    }

    /// <summary>"YYYY-MM".</summary>
    public static string Format(int year, int month)
        => string.Create(CultureInfo.InvariantCulture, $"{year:0000}-{month:00}");

    /// <summary>
    /// A period printed as ONE token or a short phrase: "09/2026", "9/26", "09.2026", "2026-09", "Sep-26",
    /// "Sep/2026", "Septiembre 2026", "Oct 2026". Null when it is not a period.
    /// </summary>
    public static string? Parse(string? text)
    {
        var key = NosisReportText.Normalise(text);
        if (key.Length == 0)
            return null;

        var numeric = NumericPeriod.Match(key);
        if (numeric.Success)
        {
            // "09/2026", "9/26", "09.2026" (the arrears table; a dot only with a 4-digit year, so a
            // decimal like "12.34" is never a period), "2026-09".
            int month, year;
            if (numeric.Groups[1].Success)
            {
                month = int.Parse(numeric.Groups[1].Value, CultureInfo.InvariantCulture);
                year  = Year(numeric.Groups[2].Success ? numeric.Groups[2].Value : numeric.Groups[3].Value);
            }
            else
            {
                year  = int.Parse(numeric.Groups[4].Value, CultureInfo.InvariantCulture);
                month = int.Parse(numeric.Groups[5].Value, CultureInfo.InvariantCulture);
            }

            return Valid(year, month) ? Format(year, month) : null;
        }

        var named = NamedPeriod.Match(key);
        if (named.Success && Month(named.Groups[1].Value) is { } namedMonth)
        {
            var year = Year(named.Groups[2].Value);
            return Valid(year, namedMonth) ? Format(year, namedMonth) : null;
        }

        return null;
    }

    /// <summary>A 4-digit year 2000-2099 printed alone ("2026"), or null.</summary>
    public static int? YearToken(string? token)
    {
        var t = token?.Trim();
        return t is { Length: 4 } && int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
               && year is >= 2000 and <= 2099
            ? year
            : null;
    }

    /// <summary>Adds <paramref name="months"/> (may be negative) to a "YYYY-MM" period.</summary>
    public static string AddMonths(string period, int months)
    {
        var date = new DateOnly(
            int.Parse(period[..4], CultureInfo.InvariantCulture),
            int.Parse(period[5..], CultureInfo.InvariantCulture), 1).AddMonths(months);
        return Format(date.Year, date.Month);
    }

    private static int Year(string digits)
    {
        var value = int.Parse(digits, CultureInfo.InvariantCulture);
        return digits.Length == 2 ? 2000 + value : value;
    }

    private static bool Valid(int year, int month) => year is >= 1990 and <= 2100 && month is >= 1 and <= 12;
}
