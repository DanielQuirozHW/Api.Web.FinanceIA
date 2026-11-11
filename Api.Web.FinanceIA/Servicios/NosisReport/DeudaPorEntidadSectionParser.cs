// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using Api.Web.FinanceIA.Models.Nosis;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// <c>deudaPorEntidad</c>: INFORMACIÓN BANCARIA / BUREAU DE CRÉDITO ("Antecedentes Crediticios"). Month
/// columns under a year row; first the aggregate rows (Situación, Comp. mensuales, Endeudamiento — not
/// entities, skipped), then one row per financial entity: its name (cut short with "…" at times, or
/// alone on the line above its figures) and per month the situación and/or the amount. The 12-month
/// table repeats further down for the next 12 months; both merge per entity. Amounts are normalised to
/// PESOS when the section (or its group) is in "miles de $".
/// <para>
/// The section runs on for pages after the tables. A line the month columns cannot place, or whose
/// cells are not figures, is SKIPPED, not fatal; the section is unreadable only when no entity row is read.
/// </para>
/// </summary>
internal static class DeudaPorEntidadSectionParser
{
    /// <summary>Header words and aggregate rows: never an entity.</summary>
    private static readonly HashSet<string> NotEntities = new(StringComparer.Ordinal)
    {
        "TOTAL", "TOTALES", "TOTAL GENERAL", "ENTIDAD", "ENTIDADES", "ENTIDAD FINANCIERA", "SIT", "SIT.", "SITUACION", "MONTO",
        "COMP. MENSUALES", "COMP MENSUALES", "COMPROMISOS MENSUALES", "ENDEUDAMIENTO", "ENDEUDAMIENTO TOTAL",
    };

    public static IReadOnlyList<NosisReportDeudaEntidad> Parse(NosisReportSection section, DateTime reportDate)
    {
        var lines = section.Lines;
        var entities = new List<(string Name, SortedDictionary<string, NosisReportDeudaPeriodo> Periods)>();
        NosisReportMonthColumns? columns = null;
        string? pendingName = null;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            if (NosisReportMonthColumns.IsHeader(lines, i))
            {
                columns = NosisReportMonthColumns.Find(lines, reportDate, i)!;
                pendingName = null;
                continue;
            }

            if (columns is null || NosisReportMonthColumns.IsYearLine(line) || NosisReportTableLines.IsNote(line))
                continue;

            if (line.Words.All(w => NotEntities.Contains(NosisReportText.Normalise(w.Text))))
                continue;   // "Sit. Monto Sit. Monto …" sub-header

            if (!columns.TryRead(line, out var label, out var cells))
            {
                pendingName = null;
                continue;   // not a row of this table
            }

            var name = label.Trim();
            if (cells.Count == 0)
            {
                // A name alone on its line: its figures follow on the next line, with no label.
                pendingName = name.Length > 0 && !IsAggregate(name) ? name : null;
                continue;
            }

            if (name.Length == 0)
                name = pendingName ?? string.Empty;
            pendingName = null;

            if (name.Length == 0 || IsAggregate(name))
                continue;

            var parsed = new List<NosisReportDeudaPeriodo>();
            var readable = true;
            foreach (var (index, words) in cells)
            {
                if (Cell(columns.Columns[index].Period, words, section) is { } cell)
                    parsed.Add(cell);
                else
                    readable = false;
            }

            if (!readable)
                continue;   // a cell that is not a figure: not an entity row

            var existing = entities.FindIndex(e => string.Equals(e.Name, name, StringComparison.Ordinal));
            var periods = existing >= 0 ? entities[existing].Periods : new SortedDictionary<string, NosisReportDeudaPeriodo>(StringComparer.Ordinal);
            if (existing < 0)
                entities.Add((name, periods));

            foreach (var cell in parsed)
                periods[cell.Periodo] = cell;
        }

        if (entities.Count == 0)
        {
            if (lines.Count == 0 || NosisReportText.SaysNone(lines))
                return [];
            throw new NosisReportFormatException(columns is null ? "No month header." : "No entity row.");
        }

        return entities
            .Select(e => new NosisReportDeudaEntidad { Entidad = e.Name, Periodos = e.Periods.Values.ToList() })
            .ToList();
    }

    private static bool IsAggregate(string name) => NotEntities.Contains(NosisReportText.Normalise(name).TrimEnd(':'));

    /// <summary>One month's cell: "situación amount", or one of them. Null when it is not a figure.</summary>
    private static NosisReportDeudaPeriodo? Cell(string period, List<NosisReportWord> words, NosisReportSection section)
    {
        var tokens = words.Select(w => w.Text).ToList();
        int? situacion = null;
        decimal? monto = null;

        if (tokens.Count >= 2)
        {
            if (!NosisReportNumbers.TryInteger(tokens[0], section.NumberStyle, out situacion) || situacion is > 6)
                return null;
            if (!NosisReportNumbers.TryAmount(tokens[^1], section.NumberStyle, out monto))
                return null;
        }
        else if (NosisReportNumbers.TryInteger(tokens[0], section.NumberStyle, out var single) && single is <= 6 && tokens[0].All(char.IsAsciiDigit))
        {
            situacion = single;
        }
        else if (!NosisReportNumbers.TryAmount(tokens[0], section.NumberStyle, out monto))
        {
            return null;
        }

        return new NosisReportDeudaPeriodo
        {
            Periodo   = period,
            Situacion = situacion,
            Monto     = NosisReportNumbers.ToPesos(monto, section.InThousands),
        };
    }
}
