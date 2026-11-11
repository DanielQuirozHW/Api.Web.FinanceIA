// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using Api.Web.FinanceIA.Models.Nosis;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// <c>atrasos</c>: EVOLUCIÓN DE ATRASOS — the amount in each arrears bucket (Normal, 31 a 90, 91 a 180,
/// 181 a 365, Mayor a 365) per month, in PESOS. Read either way round: months as COLUMNS with a row per
/// bucket, or a row per period ("MM.YYYY") under named bucket columns whose header may span three lines.
/// </summary>
internal static class AtrasosSectionParser
{
    private const string Periodo = "periodo";

    private enum Bucket { Normal, D31a90, D91a180, D181a365, Mayor365 }

    private static readonly Dictionary<string, Bucket> RowLabels = new(StringComparer.Ordinal)
    {
        ["NORMAL"] = Bucket.Normal, ["SITUACION NORMAL"] = Bucket.Normal, ["HASTA 30"] = Bucket.Normal, ["0 A 30"] = Bucket.Normal,
        ["31 A 90"] = Bucket.D31a90, ["DE 31 A 90"] = Bucket.D31a90, ["31 A 90 DIAS"] = Bucket.D31a90,
        ["91 A 180"] = Bucket.D91a180, ["DE 91 A 180"] = Bucket.D91a180, ["91 A 180 DIAS"] = Bucket.D91a180,
        ["181 A 365"] = Bucket.D181a365, ["DE 181 A 365"] = Bucket.D181a365, ["181 A 365 DIAS"] = Bucket.D181a365,
        ["MAYOR A 365"] = Bucket.Mayor365, ["MAS DE 365"] = Bucket.Mayor365, ["MAYOR A 365 DIAS"] = Bucket.Mayor365,
    };

    private const string Drop = "drop";

    // The real header spans three lines ("Normal … Situación 5" / "Período … Total" / the buckets); the
    // "Situación 5" and "Total" columns are located only so their figures are dropped, never read into
    // a bucket.
    private static readonly Dictionary<string, string> HeaderPhrases = new(StringComparer.Ordinal)
    {
        ["Período"] = Periodo, ["Periodo"] = Periodo, ["Mes"] = Periodo,
        ["Normal"] = nameof(Bucket.Normal),
        ["31 a 90"] = nameof(Bucket.D31a90),
        ["91 a 180"] = nameof(Bucket.D91a180),
        ["181 a 365"] = nameof(Bucket.D181a365),
        ["Mayor a 365"] = nameof(Bucket.Mayor365), ["Más de 365"] = nameof(Bucket.Mayor365),
        ["Situación 5"] = Drop, ["Situación"] = Drop, ["Total"] = Drop,
    };

    public static IReadOnlyList<NosisReportAtrasosMes> Parse(NosisReportSection section, DateTime reportDate)
    {
        var values = new SortedDictionary<string, Dictionary<Bucket, decimal?>>(StringComparer.Ordinal);

        if (NosisReportMonthColumns.Find(section.Lines, reportDate) is not null)
            ReadMonthColumns(section, reportDate, values);
        else if (NosisReportNamedColumns.FindAcross(section.Lines, HeaderPhrases, 4, Drop) is { } named)
            ReadPeriodRows(section, named, values);
        else if (section.Lines.Count == 0 || NosisReportText.SaysNone(section.Lines))
            return [];
        else
            throw new NosisReportFormatException("No arrears table header.");

        if (values.Count == 0 && !NosisReportText.SaysNone(section.Lines))
            throw new NosisReportFormatException("No arrears row.");

        return values.Select(v => new NosisReportAtrasosMes
        {
            Periodo  = v.Key,
            Normal   = Get(v.Value, Bucket.Normal),
            D31a90   = Get(v.Value, Bucket.D31a90),
            D91a180  = Get(v.Value, Bucket.D91a180),
            D181a365 = Get(v.Value, Bucket.D181a365),
            Mayor365 = Get(v.Value, Bucket.Mayor365),
        }).ToList();
    }

    private static void ReadMonthColumns(
        NosisReportSection section, DateTime reportDate, SortedDictionary<string, Dictionary<Bucket, decimal?>> values)
    {
        var lines = section.Lines;
        NosisReportMonthColumns? columns = null;
        var any = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (NosisReportMonthColumns.IsHeader(lines, i))
            {
                columns = NosisReportMonthColumns.Find(lines, reportDate, i)!;
                continue;
            }

            if (columns is null || NosisReportMonthColumns.IsYearLine(line) || NosisReportTableLines.IsNote(line))
                continue;

            var (label, cells) = columns.Read(line);
            if (!NosisReportText.TryMatchLabel(label, RowLabels, out var bucket))
                continue;

            any = true;
            foreach (var (index, words) in cells)
            {
                var period = columns.Columns[index].Period;
                if (!values.TryGetValue(period, out var month))
                    values[period] = month = [];
                month[bucket] = Amount(string.Join(' ', words.Select(w => w.Text)), section);
            }
        }

        if (!any)
            throw new NosisReportFormatException("No arrears bucket row.");
    }

    private static void ReadPeriodRows(
        NosisReportSection section, NosisReportNamedColumns named, SortedDictionary<string, Dictionary<Bucket, decimal?>> values)
    {
        for (var i = named.HeaderIndex + 1; i < section.Lines.Count; i++)
        {
            var line = section.Lines[i];
            if (NosisReportTableLines.IsNote(line) || named.IsRepeatedHeader(line, HeaderPhrases))
                continue;

            var cells = named.Read(line);
            if (!cells.TryGetValue(Periodo, out var periodText) || NosisReportPeriods.Parse(periodText) is not { } period)
                continue;

            var month = new Dictionary<Bucket, decimal?>();
            foreach (var bucket in Enum.GetValues<Bucket>())
            {
                if (cells.TryGetValue(bucket.ToString(), out var text))
                    month[bucket] = Amount(text, section);
            }

            values[period] = month;
        }
    }

    private static decimal? Amount(string text, NosisReportSection section)
    {
        if (!NosisReportNumbers.TryAmount(text, section.NumberStyle, out var value))
            throw new NosisReportFormatException("An arrears cell is not a number.");
        return NosisReportNumbers.ToPesos(value, section.InThousands);
    }

    private static decimal? Get(Dictionary<Bucket, decimal?> month, Bucket bucket)
        => month.TryGetValue(bucket, out var value) ? value : null;
}
