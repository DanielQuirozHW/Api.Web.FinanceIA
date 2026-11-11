// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using System.Text.RegularExpressions;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// <c>marcasCantidad</c>: MARCAS REGISTRADAS — the count only: "Registra N marcas …" (the real template),
/// a printed "Cantidad"/"Total", else the rows
/// that start with a date or an acta number; "No registra" is 0. No trademark is kept.
/// </summary>
internal static class MarcasSectionParser
{
    private static readonly Regex RowStart = new(@"^(\d{1,2}[/\-.]\d{1,2}[/\-.]\d{4}|\d{3,})$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    /// <summary>"Registra 9 marcas registradas", "9 marcas".</summary>
    private static readonly Regex CountPhrase = new(@"(?:^|\s)(\d{1,4})\s+MARCAS?\b", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    public static int Parse(NosisReportSection section)
    {
        var lines = section.Lines;
        if (lines.Count == 0)
            return 0;

        foreach (var line in lines)
        {
            if (CountPhrase.Match(line.Normalised) is { Success: true } m)
                return int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        var declared = NosisReportNumbers.FirstInteger(
            NosisReportText.ValueOf(lines, "Cantidad de marcas", "Cantidad", "Total de marcas", "Total", "Marcas registradas"),
            section.NumberStyle);
        if (declared is not null)
            return declared.Value;

        var rows = lines.Count(l => l.Words.Count > 1 && RowStart.IsMatch(l.Words[0].Text));
        if (rows > 0)
            return rows;

        if (NosisReportText.SaysNone(lines))
            return 0;

        throw new NosisReportFormatException("No trademark count.");
    }
}
