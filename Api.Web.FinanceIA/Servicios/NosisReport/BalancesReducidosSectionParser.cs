// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using System.Globalization;
using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Models.Nosis;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// <c>balancesReducidos</c>: ÚLTIMOS BALANCES REDUCIDOS — a header row of closing dates ("31 Dic 2025",
/// columns read by x-position) and whitelisted line items (rows, matched by label, a cut-off label by
/// prefix, a "#3" footnote mark ignored). Each year's cell is "value  %": the value is kept. The
/// variation columns at the right are ignored. A heading with no body is an empty list. The
/// first value of an item in a column wins, and reading stops at the ratios or at the PyME size table
/// printed under them, so neither is read. Keeps the 3 newest closings, in pesos.
/// </summary>
internal static class BalancesReducidosSectionParser
{
    private const int MaxClosings = 3;
    private const double AssignShare = 0.6;
    private const double SingleColumnTolerance = 60d;

    private static readonly Regex FullDate  = new(@"^\d{1,2}[/\-.]\d{1,2}[/\-.]\d{4}$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));
    private static readonly Regex MonthYear = new(@"^(\d{1,2})[/\-](\d{4})$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    private enum Item { Ac, Anc, Ta, Pc, Pnc, Tp, Pn, Vn, Re }

    private static readonly Dictionary<string, Item> Items = new(StringComparer.Ordinal)
    {
        ["ACTIVO CORRIENTE"] = Item.Ac,
        ["ACTIVO NO CORRIENTE"] = Item.Anc,
        ["TOTAL ACTIVO"] = Item.Ta, ["ACTIVO TOTAL"] = Item.Ta, ["TOTAL DEL ACTIVO"] = Item.Ta, ["ACTIVO"] = Item.Ta,
        ["PASIVO CORRIENTE"] = Item.Pc,
        ["PASIVO NO CORRIENTE"] = Item.Pnc,
        ["TOTAL PASIVO"] = Item.Tp, ["PASIVO TOTAL"] = Item.Tp, ["TOTAL DEL PASIVO"] = Item.Tp, ["PASIVO"] = Item.Tp,
        ["PATRIMONIO NETO"] = Item.Pn,
        ["VENTAS NETAS"] = Item.Vn, ["VENTAS"] = Item.Vn, ["INGRESOS POR VENTAS"] = Item.Vn,
        ["RESULTADO DEL EJERCICIO"] = Item.Re, ["RESULTADO NETO"] = Item.Re, ["RESULTADO FINAL"] = Item.Re, ["RESULTADO"] = Item.Re,
        ["RESULT. DEL EJERCICIO"] = Item.Re, ["RESULT DEL EJERCICIO"] = Item.Re, ["RES. DEL EJERCICIO"] = Item.Re,
        ["ACT. CORRIENTE"] = Item.Ac, ["ACT. NO CORRIENTE"] = Item.Anc, ["PAS. CORRIENTE"] = Item.Pc, ["PAS. NO CORRIENTE"] = Item.Pnc,
        ["PATRIMONIO NETO TOTAL"] = Item.Pn, ["VENTAS NETAS DE BIENES Y SERVICIOS"] = Item.Vn,
    };

    /// <summary>A footnote mark printed after a label ("#3").</summary>
    private static readonly Regex Footnote = new(@"\s*#\d+\s*", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    private static readonly Regex DayMonthYearPattern = new(@"^(?<d>\d{1,2})\s*(?<m>[^\W\d_]{3,})\.?\s*(?<y>\d{4})$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    /// <summary>A word that is only a footnote mark.</summary>
    private static readonly Regex FootnoteMark = new(@"^#\d+$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    /// <summary>The ratios and the PyME size table ("Tamaño / Sector …") printed under the balances.</summary>
    private static readonly string[] StopMarkers = ["RATIOS", "TAMANO", "CATEGORIA", "TOPE", "TRAMO"];

    public static IReadOnlyList<NosisReportBalanceReducido> Parse(NosisReportSection section)
    {
        var lines = section.Lines;
        if (lines.Count == 0)
            return [];   // a heading with no body: the company has no reduced balances

        List<(DateOnly Cierre, double Center)>? columns = null;
        var values = new Dictionary<(int Column, Item Item), decimal?>();

        foreach (var line in lines)
        {
            if (StopMarkers.Any(m => line.Normalised.StartsWith(m, StringComparison.Ordinal) || line.Normalised.Contains("RATIOS", StringComparison.Ordinal)))
                break;

            if (columns is null)
            {
                columns = Header(line);
                continue;
            }

            if (NosisReportTableLines.IsNote(line))
                continue;

            var (label, cells) = Read(line, columns);
            if (!NosisReportText.TryMatchLabel(Footnote.Replace(label, " ").Trim(), Items, out var item))
                continue;

            foreach (var (column, tokens) in cells)
            {
                if (values.ContainsKey((column, item)))
                    continue;   // the first value of an item wins

                // A year's cell holds "value  %": the value is its first figure.
                if (!NosisReportNumbers.TryAmount(tokens[0], section.NumberStyle, out var amount))
                    throw new NosisReportFormatException("A balance figure is not a number.");
                values[(column, item)] = NosisReportNumbers.ToPesos(amount, section.InThousands);
            }
        }

        if (columns is null || values.Count == 0)
        {
            if (NosisReportText.SaysNone(lines))
                return [];
            throw new NosisReportFormatException(columns is null ? "No closing-date header." : "No balance item.");
        }

        decimal? Get(int c, Item i) => values.TryGetValue((c, i), out var v) ? v : null;

        return columns
            .Select((col, c) => (col.Cierre, Index: c))
            .Where(x => values.Keys.Any(k => k.Column == x.Index))
            .OrderByDescending(x => x.Cierre)
            .Take(MaxClosings)
            .OrderBy(x => x.Cierre)
            .Select(x => new NosisReportBalanceReducido
            {
                Cierre             = x.Cierre,
                ActivoCorriente    = Get(x.Index, Item.Ac),
                ActivoNoCorriente  = Get(x.Index, Item.Anc),
                TotalActivo        = Get(x.Index, Item.Ta),
                PasivoCorriente    = Get(x.Index, Item.Pc),
                PasivoNoCorriente  = Get(x.Index, Item.Pnc),
                TotalPasivo        = Get(x.Index, Item.Tp),
                PatrimonioNeto     = Get(x.Index, Item.Pn),
                VentasNetas        = Get(x.Index, Item.Vn),
                ResultadoEjercicio = Get(x.Index, Item.Re),
            })
            .ToList();
    }

    /// <summary>
    /// The closing dates of a header row, by x; null when the row has none. A date is one token
    /// ("31/12/2025", "12/2025", "2025") or three ("31 Dic 2025", as the real template prints it).
    /// </summary>
    private static List<(DateOnly Cierre, double Center)>? Header(NosisReportLine line)
    {
        var words = line.Words;
        var dates = new List<(DateOnly Cierre, double Center)>();
        var used = 0;

        for (var i = 0; i < words.Count; i++)
        {
            // "31 Dic 2025" as three words, or two or one when the extraction glued them ("31Dic 2025").
            var grouped = false;
            for (var span = Math.Min(3, words.Count - i); span >= 1 && !grouped; span--)
            {
                if (DayMonthYear(string.Join(' ', words.Skip(i).Take(span).Select(w => w.Text))) is not { } date)
                    continue;

                dates.Add((date, (words[i].Left + words[i + span - 1].Right) / 2d));
                used += span;
                i += span - 1;
                grouped = true;
            }

            if (grouped)
                continue;

            if (Closing(words[i].Text) is { } single)
            {
                dates.Add((single, words[i].Center));
                used++;
            }
        }

        if (dates.Count == 0 || used * 2 < words.Count - 2)
            return null;

        return dates;
    }

    /// <summary>"31 Dic 2025" (or "31Dic 2025", "31Dic2025") → 2025-12-31.</summary>
    private static DateOnly? DayMonthYear(string text)
    {
        var match = DayMonthYearPattern.Match(text);
        if (!match.Success
            || !int.TryParse(match.Groups["d"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var d)
            || NosisReportPeriods.Month(match.Groups["m"].Value) is not { } m
            || NosisReportPeriods.YearToken(match.Groups["y"].Value) is not { } y)
            return null;

        return d >= 1 && d <= DateTime.DaysInMonth(y, m) ? new DateOnly(y, m, d) : null;
    }

    private static DateOnly? Closing(string token)
    {
        var t = token.Trim();
        if (FullDate.IsMatch(t))
            return NosisReportText.FirstDate(t);

        var monthYear = MonthYear.Match(t);
        if (monthYear.Success)
        {
            var month = int.Parse(monthYear.Groups[1].Value, CultureInfo.InvariantCulture);
            var year  = int.Parse(monthYear.Groups[2].Value, CultureInfo.InvariantCulture);
            return month is >= 1 and <= 12 ? new DateOnly(year, month, DateTime.DaysInMonth(year, month)) : null;
        }

        return NosisReportPeriods.YearToken(t) is { } y ? new DateOnly(y, 12, 31) : null;
    }

    /// <summary>
    /// The row's label and, per closing column, its tokens in order. Words far from every column (the
    /// variation columns at the right, notes) are ignored.
    /// </summary>
    private static (string Label, List<(int Column, List<string> Tokens)> Cells) Read(
        NosisReportLine line, List<(DateOnly Cierre, double Center)> columns)
    {
        var centers = columns.Select(c => c.Center).OrderBy(c => c).ToList();
        var pitch = centers.Count > 1 ? centers.Zip(centers.Skip(1), (a, b) => b - a).Min() : SingleColumnTolerance / AssignShare;
        var tolerance = AssignShare * pitch;
        var firstEdge = centers[0] - tolerance;

        var label = new List<string>();
        var cells = new SortedDictionary<int, List<string>>();
        foreach (var word in line.Words)
        {
            // A footnote mark ("#3") belongs to the label, wherever it reaches.
            if (word.Right < firstEdge || (cells.Count == 0 && FootnoteMark.IsMatch(word.Text)))
            {
                label.Add(word.Text);
                continue;
            }

            var best = Enumerable.Range(0, columns.Count).MinBy(c => Math.Abs(columns[c].Center - word.Center));
            if (Math.Abs(columns[best].Center - word.Center) > tolerance)
            {
                if (cells.Count == 0 && !word.Text.Any(char.IsDigit))
                    label.Add(word.Text);
                continue;
            }

            if (!cells.TryGetValue(best, out var list))
                cells[best] = list = [];
            list.Add(word.Text);
        }

        return (string.Join(' ', label), cells.Select(c => (c.Key, c.Value)).ToList());
    }
}
