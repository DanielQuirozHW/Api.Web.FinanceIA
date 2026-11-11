// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using System.Globalization;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// The report's header block prints its headline figures as a ROW OF LABELS ("SCORE", "SITUACIÓN BCRA",
/// …) with each value in a row below, aligned under its label. The same area draws the score gauge,
/// whose scale ends ("1", "999") and needle value are printed nearby but NOT under the label — so a
/// value is taken only from the first row below the label (same page, within <see cref="RowsBelow"/>
/// lines) holding a number whose centre sits under the label's centre. Nothing else of the block is
/// read. Each figure is stored as printed: the header's situación is never derived from the 24-month
/// evolution, nor the other way round.
/// </summary>
internal static class NosisReportHeaderRow
{
    private const int RowsBelow = 4;
    private const double MinTolerance = 10d;

    /// <summary>Score (1-999) and BCRA situación (0-6) from the first label row that has them.</summary>
    public static (int? Score, int? Situacion) Read(IEnumerable<IReadOnlyList<NosisReportLine>> sources)
    {
        int? score = null, situacion = null;

        foreach (var lines in sources)
        {
            score     ??= ValueUnder(lines, ["SCORE"], min: 1, max: 999);
            situacion ??= ValueUnder(lines, ["SITUACION", "BCRA"], min: 0, max: 6);
        }

        return (score, situacion);
    }

    private static int? ValueUnder(IReadOnlyList<NosisReportLine> lines, string[] label, int min, int max)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (FindLabel(lines[i], label) is not { } span)
                continue;

            var center = (span.Left + span.Right) / 2d;
            var tolerance = Math.Max(MinTolerance, ((span.Right - span.Left) / 2d) + 4d);

            for (var j = i + 1; j < lines.Count && j <= i + RowsBelow && lines[j].Page == lines[i].Page; j++)
            {
                var best = lines[j].Words
                    .Select(w => (Word: w, Distance: Math.Abs(w.Center - center)))
                    .Where(w => w.Distance <= tolerance)
                    .OrderBy(w => w.Distance)
                    .Select(w => int.TryParse(w.Word.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : (int?)null)
                    .FirstOrDefault(v => v is not null);

                if (best is { } value && value >= min && value <= max)
                    return value;
            }
        }

        return null;
    }

    /// <summary>
    /// The span of <paramref name="label"/> as whole words, in capitals, standing alone — not followed
    /// by ":" or a value on the same line (that is the "Score: 750" label line, read elsewhere).
    /// </summary>
    private static (double Left, double Right)? FindLabel(NosisReportLine line, string[] label)
    {
        var words = line.Words;
        for (var start = 0; start + label.Length <= words.Count; start++)
        {
            var match = true;
            for (var t = 0; t < label.Length && match; t++)
            {
                var text = words[start + t].Text;
                match = string.Equals(NosisReportText.Normalise(text), label[t], StringComparison.Ordinal)
                        && text == text.ToUpperInvariant();
            }

            if (!match)
                continue;

            var next = start + label.Length;
            if (next < words.Count && (words[next].Text.StartsWith(':') || int.TryParse(words[next].Text, out _)))
                continue;

            return (words[start].Left, words[start + label.Length - 1].Right);
        }

        return null;
    }
}
