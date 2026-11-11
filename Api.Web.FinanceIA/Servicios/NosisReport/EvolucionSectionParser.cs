// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using Api.Web.FinanceIA.Models.Nosis;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// <c>evolucion24m</c>: EVOLUCIÓN ANTECEDENTES, a table whose COLUMNS are months under year headers and
/// whose ROWS are Score, Cantidad de bancos, Situación, Cantidad de tarjetas, the cheque rows
/// (Rechazados / Recuperados / No recuperados / No pago multa / Positivos), Aportes Patronales Empleador
/// (P / PP / I), Referencias Comerciales and Consultas recibidas. Unknown rows are ignored; a known row
/// whose cell is not a number (or P/PP/I) fails the section.
/// </summary>
internal static class EvolucionSectionParser
{
    private static readonly string[] GroupPrefixes =
    [
        "CHEQUES RECHAZADOS BCRA ", "CHEQUES RECHAZADOS DEL BCRA ", "CHEQUES RECHAZADOS ", "CHEQUES ",
        "APORTES PATRONALES ",
    ];

    private enum Row
    {
        Score, CantidadBancos, Situacion, CantidadTarjetas, Rechazados, Recuperados, NoRecuperados,
        NoPagoMulta, Positivos, Empleador, ReferenciasComerciales, ConsultasRecibidas,
    }

    private static readonly Dictionary<string, Row> Rows = new(StringComparer.Ordinal)
    {
        ["SCORE"] = Row.Score,
        ["CANTIDAD DE BANCOS"] = Row.CantidadBancos, ["BANCOS"] = Row.CantidadBancos,
        ["SITUACION"] = Row.Situacion, ["PEOR SITUACION"] = Row.Situacion, ["SITUACION BCRA"] = Row.Situacion,
        ["CANTIDAD DE TARJETAS"] = Row.CantidadTarjetas, ["TARJETAS"] = Row.CantidadTarjetas,
        ["RECHAZADOS"] = Row.Rechazados,
        ["RECUPERADOS"] = Row.Recuperados,
        ["NO RECUPERADOS"] = Row.NoRecuperados,
        ["NO PAGO MULTA"] = Row.NoPagoMulta, ["NO PAGO DE MULTA"] = Row.NoPagoMulta,
        ["POSITIVOS"] = Row.Positivos,
        ["EMPLEADOR"] = Row.Empleador, ["EMPLEADOR P/PP/I"] = Row.Empleador,
        ["REFERENCIAS COMERCIALES"] = Row.ReferenciasComerciales,
        ["CONSULTAS RECIBIDAS"] = Row.ConsultasRecibidas, ["CONSULTAS"] = Row.ConsultasRecibidas,
    };

    public static IReadOnlyList<NosisReportEvolucionMes> Parse(NosisReportSection section, DateTime reportDate)
    {
        var lines = section.Lines;
        if (lines.Count == 0)
            return [];

        var months = new SortedDictionary<string, Dictionary<Row, object?>>(StringComparer.Ordinal);
        NosisReportMonthColumns? columns = null;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            if (NosisReportMonthColumns.IsHeader(lines, i))
            {
                // A first header, or the table repeated after a page break: its own columns.
                columns = NosisReportMonthColumns.Find(lines, reportDate, i)!;
                foreach (var column in columns.Columns)
                    months.TryAdd(column.Period, []);
                continue;
            }

            if (columns is null || NosisReportMonthColumns.IsYearLine(line) || NosisReportTableLines.IsNote(line))
                continue;

            var (label, cells) = columns.Read(line);
            var row = RowOf(label, cells.Count > 0);
            if (row is null)
                continue;

            foreach (var (index, words) in cells)
            {
                var text = string.Join(' ', words.Select(w => w.Text));
                months[columns.Columns[index].Period][row.Value] = Value(row.Value, text, section.NumberStyle);
            }
        }

        if (columns is null)
            throw new NosisReportFormatException("No month header.");

        if (months.Values.All(m => m.Count == 0))
            throw new NosisReportFormatException("No known row.");

        return months.Select(m => new NosisReportEvolucionMes
        {
            Periodo                = m.Key,
            Score                  = Int(m.Value, Row.Score),
            CantidadBancos         = Int(m.Value, Row.CantidadBancos),
            Situacion              = Int(m.Value, Row.Situacion),
            CantidadTarjetas       = Int(m.Value, Row.CantidadTarjetas),
            ChequesRechazados      = Int(m.Value, Row.Rechazados),
            ChequesRecuperados     = Int(m.Value, Row.Recuperados),
            ChequesNoRecuperados   = Int(m.Value, Row.NoRecuperados),
            ChequesNoPagoMulta     = Int(m.Value, Row.NoPagoMulta),
            ChequesPositivos       = Int(m.Value, Row.Positivos),
            AportesEmpleador       = m.Value.TryGetValue(Row.Empleador, out var e) ? e as string : null,
            ReferenciasComerciales = Int(m.Value, Row.ReferenciasComerciales),
            ConsultasRecibidas     = Int(m.Value, Row.ConsultasRecibidas),
        }).ToList();
    }

    private static Row? RowOf(string label, bool hasCells)
    {
        var key = NosisReportText.Normalise(label).TrimEnd(':');
        if (NosisReportText.TryMatchKey(key, Rows, out var direct))
            return direct;

        foreach (var prefix in GroupPrefixes)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var rest = key[prefix.Length..].Trim();
            if (NosisReportText.TryMatchKey(rest, Rows, out var found))
                return found;
        }

        // "Cheques rechazados" alone, WITH figures, is the Rechazados row itself.
        if (hasCells && key is "CHEQUES RECHAZADOS" or "CHEQUES RECHAZADOS BCRA" or "CHEQUES RECHAZADOS DEL BCRA")
            return Row.Rechazados;

        return null;
    }

    private static object? Value(Row row, string text, NosisNumberStyle style)
    {
        if (row == Row.Empleador)
        {
            var key = NosisReportText.Normalise(text);
            return key switch
            {
                "P" or "PP" or "I" => key,
                "-" or "–" or "—" => null,
                _ => throw new NosisReportFormatException("An employer-contribution cell is not P, PP or I."),
            };
        }

        if (!NosisReportNumbers.TryInteger(text, style, out var value))
            throw new NosisReportFormatException("A cell is not a whole number.");

        return value;
    }

    private static int? Int(Dictionary<Row, object?> values, Row row)
        => values.TryGetValue(row, out var value) ? value as int? : null;
}
