// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using Api.Web.FinanceIA.Models.Nosis;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// <c>juicios</c>: JUICIOS COMO DEMANDADO — the count and, per lawsuit, its date, type and jurisdiction.
/// The actor (a person or company suing) and the case number are located as columns only so their
/// words are DROPPED; they never reach the model. A row without a date continues the one above it. The
/// real template prints one block per lawsuit instead ("Fecha Inicio:", the court, "Carátula:", the actor,
/// "Objeto:"): only the date, the court cut before its case number, and the objeto are read.
/// </summary>
internal static class JuiciosSectionParser
{
    private const string Fecha = "fecha";
    private const string Tipo  = "tipo";
    private const string Jur   = "jurisdiccion";
    private const string Drop  = "drop";

    private static readonly Dictionary<string, string> HeaderPhrases = new(StringComparer.Ordinal)
    {
        ["Fecha"] = Fecha, ["Fecha de inicio"] = Fecha, ["Fecha inicio"] = Fecha,
        ["Tipo"] = Tipo, ["Tipo de juicio"] = Tipo, ["Objeto"] = Tipo, ["Causa"] = Tipo,
        ["Jurisdicción"] = Jur, ["Fuero"] = Jur, ["Juzgado"] = Jur,
        ["Actor"] = Drop, ["Demandante"] = Drop, ["Parte actora"] = Drop, ["Carátula"] = Drop,
        ["Expediente"] = Drop, ["Nro. expediente"] = Drop, ["Nro expediente"] = Drop, ["N° expediente"] = Drop, ["Expte"] = Drop,
        ["Monto"] = Drop, ["Estado"] = Drop,
    };

    public static NosisReportJuicios Parse(NosisReportSection section)
    {
        var lines = section.Lines;
        var declared = NosisReportNumbers.FirstInteger(
            NosisReportText.ValueOf(lines, "Cantidad de juicios", "Cantidad", "Total de juicios", "Total"), section.NumberStyle);

        var items = new List<(DateOnly? Fecha, List<string> Tipo, List<string> Jur)>();
        var named = NosisReportNamedColumns.Find(lines, HeaderPhrases, 2);

        if (lines.Any(l => StartDate(l) is not null))
        {
            // The real template: one block per lawsuit, "Fecha Inicio: dd/mm/yyyy", the court on the next
            // line, then "Carátula:", the actor (never read) and "Objeto: …".
            for (var i = 0; i < lines.Count; i++)
            {
                if (StartDate(lines[i]) is { } date)
                {
                    var court = i + 1 < lines.Count ? Court(lines[i + 1]) : null;
                    items.Add((date, [], court is null ? [] : [court]));
                    continue;
                }

                if (items.Count > 0 && NosisReportText.ValueAfter(lines[i], "OBJETO") is { } objeto)
                {
                    var cut = objeto.IndexOf('(', StringComparison.Ordinal);
                    items[^1].Tipo.Add((cut >= 0 ? objeto[..cut] : objeto).Trim().TrimEnd(',', '.'));
                }
            }
        }
        else if (named is not null && named.Columns.Any(c => c.Key == Fecha))
        {
            for (var i = named.HeaderIndex + 1; i < lines.Count; i++)
            {
                var line = lines[i];
                if (NosisReportTableLines.IsNote(line) || named.IsRepeatedHeader(line, HeaderPhrases)
                    || NosisReportText.ValueAfter(line, "CANTIDAD") is not null || NosisReportText.ValueAfter(line, "TOTAL") is not null)
                    continue;

                var cells = named.Read(line);
                var date = NosisReportText.FirstDate(cells.GetValueOrDefault(Fecha));
                if (date is not null)
                {
                    items.Add((date, [], []));
                }
                else if (items.Count == 0)
                {
                    continue;
                }

                var item = items[^1];
                if (cells.GetValueOrDefault(Tipo) is { Length: > 0 } tipo)
                    item.Tipo.Add(tipo);
                if (cells.GetValueOrDefault(Jur) is { Length: > 0 } jur)
                    item.Jur.Add(jur);
            }
        }
        else if (declared is null && lines.Count > 0 && !NosisReportText.SaysNone(lines))
        {
            throw new NosisReportFormatException("No lawsuit table header and no count.");
        }

        return new NosisReportJuicios
        {
            ComoDemandadoCantidad = declared ?? items.Count,
            Items = items.Select(i => new NosisReportJuicio
            {
                Fecha        = i.Fecha,
                Tipo         = i.Tipo.Count == 0 ? null : string.Join(' ', i.Tipo),
                Jurisdiccion = i.Jur.Count == 0 ? null : string.Join(' ', i.Jur),
            }).ToList(),
        };
    }

    /// <summary>The date of a "Fecha Inicio: dd/mm/yyyy" / "Fecha de inicio:" line, else null.</summary>
    private static DateOnly? StartDate(NosisReportLine line)
        => NosisReportText.FirstDate(NosisReportText.ValueAfter(line, "FECHA INICIO") ?? NosisReportText.ValueAfter(line, "FECHA DE INICIO"));

    /// <summary>
    /// The court line under "Fecha Inicio", cut before the secretariat / case number ("Secr.", "Expte",
    /// "N°", "Nro") so no case number is kept. Null for a label line or an all-caps line (an actor).
    /// </summary>
    private static string? Court(NosisReportLine line)
    {
        if (line.Words.Count == 0 || line.Words[0].Text.EndsWith(':') || line.Text == line.Text.ToUpperInvariant())
            return null;

        var kept = new List<string>();
        foreach (var word in line.Words)
        {
            var key = NosisReportText.Normalise(word.Text);
            if (key.StartsWith("SECR", StringComparison.Ordinal) || key.StartsWith("EXP", StringComparison.Ordinal)
                || key.StartsWith("N°", StringComparison.Ordinal) || key.StartsWith("NRO", StringComparison.Ordinal)
                || key.StartsWith("NUMERO", StringComparison.Ordinal))
                break;
            kept.Add(word.Text);
        }

        var court = string.Join(' ', kept).Trim().TrimEnd(',', '.', ';').Trim();
        return court.Length == 0 ? null : court;
    }
}
