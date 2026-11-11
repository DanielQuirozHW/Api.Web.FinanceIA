// Project : Api.Web.FinanceIA
// Created : 2026-11-10

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// A table with a header line of NAMED columns ("Período  Seguridad Social  Obra Social  Contribución
/// Obra Social", "Fecha  Tipo  Jurisdicción  Actor  Expediente"). Header phrases are matched as whole
/// normalised words, longest first, so "CONTRIBUCION OBRA SOCIAL" is never taken for "OBRA SOCIAL".
/// A row's word goes to the last column whose header starts at or left of the word's centre. Columns the caller does not ask for
/// are still located — so their words land there and are DROPPED, never leaking into a kept column.
/// </summary>
internal sealed class NosisReportNamedColumns
{
    private NosisReportNamedColumns(int headerIndex, IReadOnlyList<(string Key, double Left, double Center)> columns)
    {
        HeaderIndex = headerIndex;
        Columns     = columns;
    }

    public int HeaderIndex { get; }

    /// <summary>Located columns, left to right: the caller's key, header left edge, header centre.</summary>
    public IReadOnlyList<(string Key, double Left, double Center)> Columns { get; }

    /// <summary>
    /// The first line holding at least <paramref name="minimum"/> of the given header phrases.
    /// <paramref name="phrases"/> maps a header phrase (any spelling; normalised here) to a column key;
    /// several phrases may share a key. Null when no line qualifies.
    /// </summary>
    public static NosisReportNamedColumns? Find(
        IReadOnlyList<NosisReportLine> lines, IReadOnlyDictionary<string, string> phrases, int minimum)
    {
        var ordered = Order(phrases);
        for (var i = 0; i < lines.Count; i++)
        {
            var found = Match(lines[i], ordered);
            if (found.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count() >= minimum)
                return new NosisReportNamedColumns(i, found.OrderBy(f => f.Left).ToList());
        }

        return null;
    }

    /// <summary>
    /// A header printed over up to <paramref name="span"/> consecutive lines ("Normal … Situación 5" /
    /// "Período … Total" / "31 a 90 | 91 a 180 | …"): the columns of those lines merged, once at least
    /// <paramref name="minimum"/> distinct keys other than <paramref name="ignoredKey"/> are found.
    /// <see cref="HeaderIndex"/> is the header's last line.
    /// </summary>
    public static NosisReportNamedColumns? FindAcross(
        IReadOnlyList<NosisReportLine> lines, IReadOnlyDictionary<string, string> phrases, int minimum,
        string? ignoredKey = null, int span = 3)
    {
        var ordered = Order(phrases);
        for (var i = 0; i < lines.Count; i++)
        {
            var found = new List<(string Key, double Left, double Center)>();
            for (var k = 0; k < span && i + k < lines.Count; k++)
            {
                foreach (var column in Match(lines[i + k], ordered))
                {
                    if (column.Key == ignoredKey || found.All(f => f.Key != column.Key))
                        found.Add(column);
                }

                var keys = found.Select(f => f.Key).Where(key => key != ignoredKey).Distinct(StringComparer.Ordinal).Count();
                if (keys >= minimum)
                    return new NosisReportNamedColumns(i + k, found.OrderBy(f => f.Left).ToList());
            }
        }

        return null;
    }

    /// <summary>Phrases tokenised like words (normalised, trailing "."/":" trimmed), longest first.</summary>
    private static List<(string[] Tokens, string Key)> Order(IReadOnlyDictionary<string, string> phrases)
        => phrases
            .Select(p => (Tokens: NosisReportText.Normalise(p.Key).Split(' ').Select(t => t.TrimEnd(':', '.')).ToArray(), Key: p.Value))
            .OrderByDescending(p => p.Tokens.Length)
            .ToList();

    /// <summary>The phrases one line holds, each matched once on unused whole words.</summary>
    private static List<(string Key, double Left, double Center)> Match(NosisReportLine line, List<(string[] Tokens, string Key)> ordered)
    {
        var words = line.Words;
        var normalised = words.Select(w => NosisReportText.Normalise(w.Text).TrimEnd(':', '.')).ToArray();
        var used = new bool[words.Count];
        var found = new List<(string Key, double Left, double Center)>();

        foreach (var (tokens, key) in ordered)
        {
            for (var start = 0; start + tokens.Length <= words.Count; start++)
            {
                var match = true;
                for (var t = 0; t < tokens.Length && match; t++)
                    match = !used[start + t] && string.Equals(normalised[start + t], tokens[t], StringComparison.Ordinal);

                if (!match)
                    continue;

                for (var t = 0; t < tokens.Length; t++)
                    used[start + t] = true;

                var left  = words[start].Left;
                var right = words[start + tokens.Length - 1].Right;
                found.Add((key, left, (left + right) / 2d));
                break;
            }
        }

        return found;
    }

    /// <summary>True when the line repeats this header (a table continued after a page break).</summary>
    public bool IsRepeatedHeader(NosisReportLine line, IReadOnlyDictionary<string, string> phrases)
    {
        var again = Find([line], phrases, Math.Min(2, Columns.Count));
        return again is not null;
    }

    private const double HeaderTolerance = 2d;

    /// <summary>The row's text per column key (words joined in order). Words left of every column go to the first.</summary>
    public Dictionary<string, string> Read(NosisReportLine line)
    {
        var cells = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var word in line.Words)
        {
            // Left-aligned cells: a word belongs to the last column whose header starts at or left of
            // the word's centre (a small tolerance for a figure printed a hair left of its header).
            var best = Columns[0];
            foreach (var column in Columns)
            {
                if (column.Left - HeaderTolerance <= word.Center)
                    best = column;
            }

            if (!cells.TryGetValue(best.Key, out var list))
                cells[best.Key] = list = [];
            list.Add(word.Text);
        }

        return cells.ToDictionary(c => c.Key, c => string.Join(' ', c.Value), StringComparer.Ordinal);
    }
}
