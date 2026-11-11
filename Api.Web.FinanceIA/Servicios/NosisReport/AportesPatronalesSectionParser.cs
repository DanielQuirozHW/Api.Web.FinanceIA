// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using Api.Web.FinanceIA.Models.Nosis;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// <c>aportesPatronales</c>: APORTES PATRONALES ("Como EMPLEADOR al …") — per period, the state of the
/// aportes de seguro social, aportes de obra social and contribución patronal obra social as the report
/// words it ("Pago", "Pago Parcial", "Impago", …). Read either way
/// round: a row per period under named columns, or months as columns with a row per item.
/// </summary>
internal static class AportesPatronalesSectionParser
{
    private const string Periodo = "periodo";
    private const string Ss      = "ss";
    private const string Os      = "os";
    private const string Cos     = "cos";

    private static readonly Dictionary<string, string> HeaderPhrases = new(StringComparer.Ordinal)
    {
        ["Período"] = Periodo, ["Periodo"] = Periodo, ["Mes"] = Periodo,
        // As the real template prints them.
        ["Aportes de seguro social"] = Ss, ["Aportes de seguridad social"] = Ss,
        ["Aportes de obra social"] = Os,
        ["Contrib. patronal obra social"] = Cos, ["Contribución patronal obra social"] = Cos,
        // Shorter wordings.
        ["Seguridad Social"] = Ss, ["Seguro social"] = Ss,
        ["Obra Social"] = Os,
        ["Contribución Obra Social"] = Cos, ["Contribuciones Obra Social"] = Cos, ["Contribución OS"] = Cos,
    };

    private static readonly Dictionary<string, string> RowLabels = new(StringComparer.Ordinal)
    {
        ["SEGURIDAD SOCIAL"] = Ss,
        ["OBRA SOCIAL"] = Os,
        ["CONTRIBUCION OBRA SOCIAL"] = Cos, ["CONTRIBUCIONES OBRA SOCIAL"] = Cos, ["CONTRIBUCION OS"] = Cos,
    };

    public static IReadOnlyList<NosisReportAportesMes> Parse(NosisReportSection section, DateTime reportDate)
    {
        var values = new SortedDictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);

        if (section.Lines.Count == 0)
            return [];

        if (NosisReportNamedColumns.FindAcross(section.Lines, HeaderPhrases, 3, span: 2) is { } named
            && named.Columns.Any(c => c.Key == Periodo))
        {
            ReadPeriodRows(section, named, values);
        }
        else if (NosisReportMonthColumns.Find(section.Lines, reportDate) is not null)
        {
            ReadMonthColumns(section, reportDate, values);
        }
        else if (NosisReportText.SaysNone(section.Lines))
        {
            return [];
        }
        else
        {
            throw new NosisReportFormatException("No contributions table header.");
        }

        if (values.Count == 0 && !NosisReportText.SaysNone(section.Lines))
            throw new NosisReportFormatException("No contributions row.");

        return values.Select(v => new NosisReportAportesMes
        {
            Periodo                = v.Key,
            SeguridadSocial        = v.Value.GetValueOrDefault(Ss),
            ObraSocial             = v.Value.GetValueOrDefault(Os),
            ContribucionObraSocial = v.Value.GetValueOrDefault(Cos),
        }).ToList();
    }

    private static void ReadPeriodRows(
        NosisReportSection section, NosisReportNamedColumns named, SortedDictionary<string, Dictionary<string, string?>> values)
    {
        for (var i = named.HeaderIndex + 1; i < section.Lines.Count; i++)
        {
            var line = section.Lines[i];
            if (NosisReportTableLines.IsNote(line) || named.IsRepeatedHeader(line, HeaderPhrases))
                continue;

            var cells = named.Read(line);
            if (!cells.TryGetValue(Periodo, out var periodText) || NosisReportPeriods.Parse(periodText) is not { } period)
                continue;

            values[period] = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [Ss]  = Text(cells.GetValueOrDefault(Ss)),
                [Os]  = Text(cells.GetValueOrDefault(Os)),
                [Cos] = Text(cells.GetValueOrDefault(Cos)),
            };
        }
    }

    private static void ReadMonthColumns(
        NosisReportSection section, DateTime reportDate, SortedDictionary<string, Dictionary<string, string?>> values)
    {
        var lines = section.Lines;
        NosisReportMonthColumns? columns = null;

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
            if (!NosisReportText.TryMatchLabel(label, RowLabels, out var item))
                continue;

            foreach (var (index, words) in cells)
            {
                var period = columns.Columns[index].Period;
                if (!values.TryGetValue(period, out var month))
                    values[period] = month = new Dictionary<string, string?>(StringComparer.Ordinal);
                month[item] = Text(string.Join(' ', words.Select(w => w.Text)));
            }
        }
    }

    private static string? Text(string? value)
        => string.IsNullOrWhiteSpace(value) || NosisReportNumbers.IsNone(value) ? null : value.Trim();
}
