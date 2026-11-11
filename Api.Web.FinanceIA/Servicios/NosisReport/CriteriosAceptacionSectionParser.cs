// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using Api.Web.FinanceIA.Models.Nosis;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// <c>criteriosAceptacion</c>: CRITERIOS DE ACEPTACIÓN — the policy's name and verdict, each criterion
/// with its result (a row whose two halves are separated by a wide gap and whose right half is a known
/// verdict), and whether the report flags
/// the block "INFORMACIÓN SIN VALIDEZ - EN PROCESO DE PRUEBA".
/// </summary>
internal static class CriteriosAceptacionSectionParser
{
    /// <summary>A horizontal gap, in points, wide enough to separate a criterion from its result.</summary>
    private const double ColumnGap = 18d;

    /// <summary>
    /// The verdicts a criterion's result may read. A two-column line whose right half is not one of
    /// these is not a criterion (it is a note, a heading row, or something this parser does not know).
    /// </summary>
    private static readonly HashSet<string> Verdicts = new(StringComparer.Ordinal)
    {
        "CUMPLE", "NO CUMPLE", "APROBADO", "RECHAZADO", "ACEPTADO", "NO ACEPTADO", "OK", "OBSERVADO",
        "PASA", "NO PASA", "SI", "NO", "APLICA", "NO APLICA", "N/A", "ALERTA", "VERDE", "AMARILLO", "ROJO",
        "CORRESPONDE", "NO CORRESPONDE", "SIN DATOS", "SIN INFORMACION", "NORMAL", "BAJO", "MEDIO", "ALTO",
    };

    // "Resultado según configuración:" is THE verdict line; the CDA name, its version line and the first
    // criterion ("Bureau de Crédito del BCRA: Rechazado") are not.
    private static readonly string[] NameLabels   =
        ["Criterio de aceptación configurado por el cliente", "Criterio de aceptación", "Criterio", "Nombre", "Política", "Modelo"];
    private static readonly string[] ResultLabels =
        ["Resultado según configuración", "Resultado final", "Resultado", "Dictamen", "Evaluación"];

    public static NosisReportCriteriosAceptacion Parse(NosisReportSection section)
    {
        var lines = section.Lines;
        var enPrueba = NosisReportText.Contains(lines, "SIN VALIDEZ") || NosisReportText.Contains(lines, "EN PROCESO DE PRUEBA");
        var nombre = NosisReportText.ValueOf(lines, NameLabels);
        var resultado = NosisReportText.ValueOf(lines, ResultLabels);

        var criterios = new List<NosisReportCriterio>();
        foreach (var line in lines)
        {
            if (NosisReportTableLines.IsNote(line) || StartsWithLabel(line) || IsHeader(line))
                continue;

            if (Split(line) is { } pair && Verdicts.Contains(NosisReportText.Normalise(pair.Right)))
                criterios.Add(new NosisReportCriterio { Nombre = pair.Left.TrimEnd(':', ' '), Resultado = pair.Right });
        }

        if (nombre is null && resultado is null && criterios.Count == 0 && !NosisReportText.SaysNone(lines))
            throw new NosisReportFormatException("No criterion.");

        return new NosisReportCriteriosAceptacion
        {
            Nombre    = nombre,
            Resultado = resultado,
            EnPrueba  = enPrueba,
            Criterios = criterios,
        };
    }

    private static bool StartsWithLabel(NosisReportLine line)
        => NameLabels.Concat(ResultLabels).Any(l => NosisReportText.ValueAfter(line, NosisReportText.Normalise(l)) is not null);

    private static bool IsHeader(NosisReportLine line)
        => line.Normalised is "CRITERIO RESULTADO" or "CRITERIOS RESULTADO" or "CRITERIO RESULTADO OBTENIDO";

    /// <summary>The line cut at its widest gap, when that gap is a column gap.</summary>
    private static (string Left, string Right)? Split(NosisReportLine line)
    {
        if (line.Words.Count < 2)
            return null;

        var at = -1;
        var widest = 0d;
        for (var i = 1; i < line.Words.Count; i++)
        {
            var gap = line.Words[i].Left - line.Words[i - 1].Right;
            if (gap > widest)
            {
                widest = gap;
                at = i;
            }
        }

        if (widest < ColumnGap)
            return null;

        return (string.Join(' ', line.Words.Take(at).Select(w => w.Text)),
                string.Join(' ', line.Words.Skip(at).Select(w => w.Text)));
    }
}
