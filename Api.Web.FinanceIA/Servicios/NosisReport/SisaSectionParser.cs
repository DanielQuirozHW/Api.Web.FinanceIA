// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using Api.Web.FinanceIA.Models.Nosis;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// <c>sisa</c>: SISA — its status and categories. Only those labels are read: the CBU (and any alias)
/// printed in the same block is never looked at.
/// </summary>
internal static class SisaSectionParser
{
    public static NosisReportSisa Parse(NosisReportSection section)
    {
        var lines = section.Lines;
        var estado = NosisReportText.ValueOf(lines, "Estado SISA", "Estado", "Situación");
        var categorias = (NosisReportText.ValueOf(lines, "Categorías", "Categoría", "Categoria SISA") ?? string.Empty)
            .Split([',', ';', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => !NosisReportNumbers.IsNone(c))
            .ToList();

        if (estado is null && categorias.Count == 0 && !NosisReportText.SaysNone(lines))
            throw new NosisReportFormatException("No SISA status or category.");

        return new NosisReportSisa { Estado = estado, Categorias = categorias };
    }
}
