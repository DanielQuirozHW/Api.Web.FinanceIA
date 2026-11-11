// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// Text helpers shared by the section parsers: normalisation, label → value lookup, dates and yes/no.
/// Pure. Nothing here logs.
/// </summary>
internal static class NosisReportText
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    private static readonly Regex DatePattern = new(
        @"(?<!\d)(\d{1,2})[/\-.](\d{1,2})[/\-.](\d{4})(?!\d)", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    /// <summary>Upper case, accents stripped (Á→A, Ñ→N), whitespace collapsed, trimmed.</summary>
    public static string Normalise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        }

        var stripped = builder.ToString().Normalize(NormalizationForm.FormC).ToUpperInvariant();
        return Whitespace.Replace(stripped, " ").Trim();
    }

    /// <summary>
    /// The value that follows <paramref name="label"/> on a line that STARTS with it ("Label: value",
    /// "Label value"), in the line's original spelling. Null when no line starts with the label or the
    /// value is empty. The first matching line wins.
    /// </summary>
    public static string? ValueOf(IReadOnlyList<NosisReportLine> lines, params string[] labels)
    {
        foreach (var label in labels)
        {
            var key = Normalise(label);
            foreach (var line in lines)
            {
                var value = ValueAfter(line, key);
                if (value is not null)
                    return value;
            }
        }

        return null;
    }

    /// <summary>
    /// The text after a normalised <paramref name="labelKey"/> when the line starts with it as whole
    /// words. Works on words, so the value keeps its original spelling. A printed label cut short with
    /// "…" or "..." ("Cuentas Personas Juríd…") matches when its stem starts the expected word.
    /// </summary>
    public static string? ValueAfter(NosisReportLine line, string labelKey)
    {
        var labelWords = labelKey.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var consumed = MatchWords(line.Words.Select(w => w.Text).ToList(), 0, labelWords, requireColonOnlyAtEnd: true);
        if (consumed is null)
            return null;

        var rest = string.Join(' ', line.Words.Skip(consumed.Value).Select(w => w.Text)).Trim();
        rest = rest.TrimStart(':').Trim();
        return rest.Length == 0 ? null : rest;
    }

    /// <summary>True when a printed word was cut short ("Juríd…", "Juríd...").</summary>
    public static bool IsTruncated(string word)
        => word.EndsWith('…') || word.EndsWith("...", StringComparison.Ordinal);

    /// <summary>The printed word without its ellipsis, normalised.</summary>
    public static string Stem(string word)
        => Normalise(word.TrimEnd('…', '.', ':')).Trim();

    /// <summary>
    /// The value <paramref name="map"/> gives a printed row label: its exact normalised text, or — when
    /// the label was cut short with an ellipsis — the one entry whose key starts with the stem (at least
    /// 4 characters). Null when nothing, or more than one value, matches.
    /// </summary>
    public static bool TryMatchLabel<T>(string printed, IReadOnlyDictionary<string, T> map, out T value)
        => TryMatchKey(Normalise(printed).TrimEnd(':'), map, out value);

    /// <summary><see cref="TryMatchLabel"/> on an already normalised label.</summary>
    public static bool TryMatchKey<T>(string key, IReadOnlyDictionary<string, T> map, out T value)
    {
        value = default!;
        if (map.TryGetValue(key, out var exact))
        {
            value = exact;
            return true;
        }

        if (!IsTruncated(key))
            return false;

        var stem = Stem(key);
        if (stem.Length < 4)
            return false;

        var candidates = map.Where(e => e.Key.StartsWith(stem, StringComparison.Ordinal)).Select(e => e.Value).Distinct().ToList();
        if (candidates.Count != 1)
            return false;

        value = candidates[0];
        return true;
    }

    /// <summary>
    /// How many printed words, from <paramref name="start"/>, spell <paramref name="labelWords"/> —
    /// exactly, or up to a truncated word whose stem starts the expected word (the rest of the label is
    /// then taken as cut off). Null when they do not.
    /// </summary>
    private static int? MatchWords(IReadOnlyList<string> printed, int start, string[] labelWords, bool requireColonOnlyAtEnd)
    {
        if (printed.Count - start < 1)
            return null;

        for (var i = 0; i < labelWords.Length; i++)
        {
            if (start + i >= printed.Count)
                return null;

            var raw = printed[start + i];
            var word = Normalise(raw).TrimEnd(':');
            if (string.Equals(word, labelWords[i], StringComparison.Ordinal))
            {
                if (requireColonOnlyAtEnd && i < labelWords.Length - 1 && raw.EndsWith(':'))
                    return null;
                continue;
            }

            var stem = Stem(raw);
            if (IsTruncated(raw.TrimEnd(':')) && stem.Length >= 3 && labelWords[i].StartsWith(stem, StringComparison.Ordinal)
                && (i > 0 || stem.Length >= 4))
                return start + i + 1;

            return null;
        }

        return start + labelWords.Length;
    }

    /// <summary>
    /// The word right after one of <paramref name="labels"/> found ANYWHERE in a line (a two-column
    /// "Score 750   Situación BCRA 1" row), a separate ":" skipped. For single-token values (numbers,
    /// Sí/No) where the label does not start the line. Null when no label is found.
    /// </summary>
    public static string? TokenAfter(IReadOnlyList<NosisReportLine> lines, params string[] labels)
    {
        foreach (var label in labels)
        {
            var tokens = Normalise(label).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var printed = line.Words.Select(w => w.Text).ToList();
                for (var start = 0; start < printed.Count; start++)
                {
                    if (MatchWords(printed, start, tokens, requireColonOnlyAtEnd: false) is not { } next)
                        continue;

                    if (next < printed.Count && printed[next].Trim() == ":")
                        next++;   // a separate ":"
                    if (next < printed.Count)
                        return printed[next].TrimStart(':');
                }
            }
        }

        return null;
    }

    /// <summary>
    /// A value printed UNDER its label: a label row ("Tipo de sociedad   Estado   Antigüedad   …") with
    /// the values on the next line, each starting at its label's left edge. Takes the words of the next
    /// line (same page) from this label's left edge up to where the next label of the row starts.
    /// Null when the label is not found or nothing sits under it.
    /// </summary>
    public static string? ValueBelow(IReadOnlyList<NosisReportLine> lines, string label)
    {
        var tokens = Normalise(label).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < lines.Count; i++)
        {
            var words = lines[i].Words;
            var printed = words.Select(w => w.Text).ToList();
            for (var start = 0; start < printed.Count; start++)
            {
                if (MatchWords(printed, start, tokens, requireColonOnlyAtEnd: false) is not { } next)
                    continue;

                var left = words[start].Left;
                var limit = double.MaxValue;
                for (var k = next; k < words.Count; k++)
                {
                    if (words[k].Left - words[k - 1].Right > LabelColumnGap)
                    {
                        limit = words[k].Left;
                        break;
                    }
                }

                var below = lines[i + 1];
                if (below.Page != lines[i].Page)
                    return null;

                var value = string.Join(' ', below.Words
                    .Where(w => w.Left >= left - 3d && w.Left < limit - 3d)
                    .Select(w => w.Text)).Trim();
                return value.Length == 0 || NosisReportNumbers.IsNone(value) ? null : value;
            }
        }

        return null;
    }

    /// <summary>A gap wider than this, in points, separates two labels of a label row.</summary>
    private const double LabelColumnGap = 8d;

    /// <summary>First dd/MM/yyyy (or dd-MM-yyyy, dd.MM.yyyy) date in the text.</summary>
    public static DateOnly? FirstDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = DatePattern.Match(text);
        return match.Success ? ToDate(match) : null;
    }

    /// <summary>Every date in the text, in order.</summary>
    public static IReadOnlyList<DateOnly> Dates(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var dates = new List<DateOnly>();
        foreach (Match match in DatePattern.Matches(text))
        {
            if (ToDate(match) is { } date)
                dates.Add(date);
        }

        return dates;
    }

    /// <summary>Sí/Si/S/Yes/Vigente → true; No/N/No vigente → false; anything else → null.</summary>
    public static bool? YesNo(string? text)
    {
        var key = Normalise(text);
        if (key.Length == 0)
            return null;

        if (key.StartsWith("NO", StringComparison.Ordinal) && (key.Length == 2 || !char.IsLetter(key[2])))
            return false;
        if (key is "N")
            return false;
        if (key is "SI" or "S" or "YES" || key.StartsWith("SI ", StringComparison.Ordinal))
            return true;

        return null;
    }

    /// <summary>True when any line's normalised text contains <paramref name="phrase"/> (normalised).</summary>
    public static bool Contains(IEnumerable<NosisReportLine> lines, string phrase)
    {
        var key = Normalise(phrase);
        return lines.Any(l => l.Normalised.Contains(key, StringComparison.Ordinal));
    }

    /// <summary>
    /// True when the section says it holds nothing ("No registra…", "Sin información", "No posee…"):
    /// an empty table that is a genuine "none", not an unreadable one.
    /// </summary>
    public static bool SaysNone(IEnumerable<NosisReportLine> lines)
        => lines.Any(l => l.Normalised.StartsWith("NO REGISTRA", StringComparison.Ordinal)
                       || l.Normalised.StartsWith("NO SE REGISTRA", StringComparison.Ordinal)
                       || l.Normalised.StartsWith("NO POSEE", StringComparison.Ordinal)
                       || l.Normalised.StartsWith("SIN INFORMACION", StringComparison.Ordinal)
                       || l.Normalised.StartsWith("SIN DATOS", StringComparison.Ordinal)
                       || l.Normalised.StartsWith("SIN REGISTROS", StringComparison.Ordinal));

    private static DateOnly? ToDate(Match match)
    {
        var day   = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var year  = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);

        if (year < 1900 || year > 2100 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
            return null;

        return new DateOnly(year, month, day);
    }
}
