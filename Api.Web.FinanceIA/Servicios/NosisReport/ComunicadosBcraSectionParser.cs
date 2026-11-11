// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Models.Nosis;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// <c>comunicadosBcra</c>: COMUNICADOS DEL BCRA Y DE FUENTES DIRECTAS — how many, and their dates. Each
/// comunicado starts with its date; only that first word of a line is ever looked at. The text (CUITs,
/// plan numbers, names) is never read into the model.
/// </summary>
internal static class ComunicadosBcraSectionParser
{
    private static readonly Regex DateWord = new(@"^\d{1,2}[/\-.]\d{1,2}[/\-.]\d{4}$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    public static NosisReportComunicadosBcra Parse(NosisReportSection section)
    {
        var lines = section.Lines;
        var declared = NosisReportNumbers.FirstInteger(
            NosisReportText.ValueOf(lines, "Cantidad de comunicados", "Cantidad", "Total"), section.NumberStyle);

        // Each comunicado prints "Fecha: dd/mm/yyyy" on its own line (or starts with its date). Only that
        // first word, or the word after "Fecha:", is ever looked at.
        var fechas = lines
            .Select(DateOf)
            .Where(d => d is not null)
            .Select(d => d!.Value)
            .Order()
            .ToList();

        if (declared is null && fechas.Count == 0 && lines.Count > 0 && !NosisReportText.SaysNone(lines))
            throw new NosisReportFormatException("No dated comunicado and no count.");

        return new NosisReportComunicadosBcra { Cantidad = declared ?? fechas.Count, Fechas = fechas };
    }

    private static DateOnly? DateOf(NosisReportLine line)
    {
        var words = line.Words;
        if (words.Count == 0)
            return null;
        if (DateWord.IsMatch(words[0].Text))
            return NosisReportText.FirstDate(words[0].Text);
        if (words.Count >= 2 && NosisReportText.Normalise(words[0].Text) == "FECHA:" && DateWord.IsMatch(words[1].Text))
            return NosisReportText.FirstDate(words[1].Text);
        return null;
    }
}
