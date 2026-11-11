// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Models.Nosis;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// <c>chequesRechazadosPorMes</c>: CHEQUES RECHAZADOS DEL BCRA. The section lists every rejected cheque
/// (it can run to hundreds of pages) grouped under month lines — "Octubre 2026  5  18,240,512.30" — each
/// followed by its per-cheque rows. ONLY the month lines are read: a per-cheque row starts with a number
/// or a date, never with a month name, so it never matches and is never put in the model.
/// <para>
/// "Total = N amount" is a PER-MONTH subtotal and is not printed for every month. When present it closes
/// the month above it and is checked against that month (a month printed twice across a page break is
/// SUMMED first); a Total that matches the sum of every month is a grand total. A Total that matches
/// neither is a misread: the section is unreadable.
/// </para>
/// </summary>
internal static class ChequesRechazadosSectionParser
{
    private static readonly Regex MonthLine = new(
        @"^(?<month>[A-Z]+)\s+(?<year>\d{4})\s+(?<count>\d{1,3}(?:[.,]\d{3})+|\d+)\s+(?<amount>\$?\s?\d[\d.,]*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    private const decimal Cent = 0.01m;

    public static IReadOnlyList<NosisReportChequesMes> Parse(NosisReportSection section)
    {
        var months = new SortedDictionary<string, (int Count, decimal Amount)>(StringComparer.Ordinal);
        string? current = null;

        foreach (var line in section.Lines)
        {
            if (line.Normalised.StartsWith("TOTAL", StringComparison.Ordinal))
            {
                CheckTotal(line.Normalised, current, months, section);
                continue;
            }

            var match = MonthLine.Match(line.Normalised);
            if (!match.Success || NosisReportPeriods.Month(match.Groups["month"].Value) is not { } monthNumber)
                continue;

            var period = NosisReportPeriods.Format(int.Parse(match.Groups["year"].Value, System.Globalization.CultureInfo.InvariantCulture), monthNumber);
            NosisReportNumbers.TryInteger(match.Groups["count"].Value, section.NumberStyle, out var count);
            NosisReportNumbers.TryAmount(match.Groups["amount"].Value.Replace(" ", string.Empty, StringComparison.Ordinal), section.NumberStyle, out var amount);

            var monto = NosisReportNumbers.ToPesos(amount, section.InThousands)
                        ?? throw new NosisReportFormatException("A month total's amount is not readable.");

            // A month printed again (its group continues after a page break): its figures add up.
            months[period] = months.TryGetValue(period, out var seen)
                ? (seen.Count + (count ?? 0), seen.Amount + monto)
                : (count ?? 0, monto);
            current = period;
        }

        if (months.Count == 0)
        {
            if (section.Lines.Count == 0 || NosisReportText.SaysNone(section.Lines))
                return [];
            throw new NosisReportFormatException("No month line.");
        }

        return months.Select(m => new NosisReportChequesMes { Periodo = m.Key, Cantidad = m.Value.Count, Monto = m.Value.Amount }).ToList();
    }

    /// <summary>"Total = 12  45,000,000.00" (count and amount) or "Total = 45,000,000.00" (amount).</summary>
    private static void CheckTotal(
        string totalLine, string? current, SortedDictionary<string, (int Count, decimal Amount)> months, NosisReportSection section)
    {
        var numbers = NosisReportNumbers.Amounts(totalLine, section.NumberStyle);
        if (numbers.Count == 0)
            return;

        var amount = NosisReportNumbers.ToPesos(numbers[^1], section.InThousands)!.Value;
        int? count = numbers.Count >= 2 ? (int)numbers[0] : null;

        bool Matches((int Count, decimal Amount) figures)
            => Math.Abs(figures.Amount - amount) <= Cent && (count is null || figures.Count == count);

        if (current is not null && Matches(months[current]))
            return;   // the month's subtotal

        var all = (months.Values.Sum(m => m.Count), months.Values.Sum(m => m.Amount));
        if (months.Count > 0 && Matches(all))
            return;   // a grand total

        throw new NosisReportFormatException("A Total line matches neither its month nor the whole section.");
    }
}
