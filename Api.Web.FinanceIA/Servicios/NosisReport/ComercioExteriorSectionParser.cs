// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using Api.Web.FinanceIA.Models.Nosis;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// <c>comercioExterior</c>: from IMPORTACIONES, EXPORTACIONES and an optional COMERCIO EXTERIOR block —
/// whether the company imports and exports, and the years its rows cover. A printed "Importador: Sí"
/// wins; otherwise a section that says "No registra" is false and one with rows is true. Only years are
/// read — a row's first word or a header row's year columns: chapter descriptions are never kept.
/// </summary>
internal static class ComercioExteriorSectionParser
{
    public static (NosisReportComercioExterior? Value, string Status) Parse(
        IReadOnlyDictionary<NosisReportHeadingKind, NosisReportSection> sections)
    {
        sections.TryGetValue(NosisReportHeadingKind.Importaciones, out var imports);
        sections.TryGetValue(NosisReportHeadingKind.Exportaciones, out var exports);
        sections.TryGetValue(NosisReportHeadingKind.ComercioExterior, out var block);

        if (imports is null && exports is null && block is null)
            return (null, NosisReportSectionKeys.StatusAbsent);

        var blockLines = block?.Lines ?? [];
        var importador = NosisReportText.YesNo(NosisReportText.ValueOf(blockLines, "Importador")) ?? Flag(imports);
        var exportador = NosisReportText.YesNo(NosisReportText.ValueOf(blockLines, "Exportador")) ?? Flag(exports);

        // The years: a row that starts with one, or a header row that names two or more ("Pos |
        // Descripción | Año 2020 2021 … 2025", the real template). No other word of a row is read.
        var anios = new[] { imports, exports, block }
            .Where(s => s is not null)
            .SelectMany(s => s!.Lines)
            .SelectMany(l => l.Words.Count(w => NosisReportPeriods.YearToken(w.Text) is not null) >= 2
                ? l.Words.Select(w => NosisReportPeriods.YearToken(w.Text))
                : l.Words.Take(1).Select(w => NosisReportPeriods.YearToken(w.Text)))
            .Where(y => y is not null)
            .Select(y => y!.Value)
            .Distinct()
            .Order()
            .ToList();

        if (importador is null && exportador is null && anios.Count == 0)
            return (null, NosisReportSectionKeys.StatusUnreadable);

        return (new NosisReportComercioExterior { Importador = importador, Exportador = exportador, Anios = anios },
                NosisReportSectionKeys.StatusOk);
    }

    /// <summary>"No registra" → false; any row under the heading → true; an empty heading → false.</summary>
    private static bool? Flag(NosisReportSection? section)
    {
        if (section is null)
            return null;
        if (section.Lines.Count == 0 || NosisReportText.SaysNone(section.Lines))
            return false;
        return section.Lines.Any(l => !NosisReportTableLines.IsNote(l) && l.Words.Count > 0);
    }
}
