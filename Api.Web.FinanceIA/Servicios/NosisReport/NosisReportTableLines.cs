// Project : Api.Web.FinanceIA
// Created : 2026-11-10

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>Line filters shared by the table parsers.</summary>
internal static class NosisReportTableLines
{
    /// <summary>
    /// A note, not a row: "Importes expresados en miles de $", "Fuente: BCRA", "Nota: …", a "No
    /// registra…" line, or the "información sin validez" banner.
    /// </summary>
    public static bool IsNote(NosisReportLine line)
    {
        var n = line.Normalised;
        return n.Contains("MILES DE", StringComparison.Ordinal)
               || n.StartsWith("IMPORTES", StringComparison.Ordinal)
               || n.StartsWith("FUENTE:", StringComparison.Ordinal)
               || n.StartsWith("NOTA", StringComparison.Ordinal)
               || n.Contains("SIN VALIDEZ", StringComparison.Ordinal)
               || n.StartsWith("NO REGISTRA", StringComparison.Ordinal)
               || n.StartsWith("NO SE REGISTRA", StringComparison.Ordinal)
               || n.StartsWith("SIN INFORMACION", StringComparison.Ordinal);
    }

    /// <summary>The cell's words as one string, or null for an empty cell.</summary>
    public static string? Cell(Dictionary<int, List<NosisReportWord>> cells, int column)
        => cells.TryGetValue(column, out var words) && words.Count > 0 ? string.Join(' ', words.Select(w => w.Text)) : null;
}
