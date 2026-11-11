// Project : Api.Web.FinanceIA
// Created : 2026-11-10

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// A table whose columns are months: the month header line ("Oct Nov Dic Ene …", or "10/24 11/24 …",
/// or "Sep-26 …") and, when the header names months only, the year line above it ("2024 2025 2026").
/// Each column is a period "YYYY-MM" at an X centre. Rows are read by assigning each word to the
/// nearest column, within 0.6 of the column pitch; words left of the first column are the row label.
/// </summary>
internal sealed class NosisReportMonthColumns
{
    private const double AssignShare = 0.6;
    private const int MinColumns = 3;
    private const int MinNumericColumns = 6;

    private NosisReportMonthColumns(int headerIndex, IReadOnlyList<(string Period, double Center)> columns, double pitch)
    {
        HeaderIndex = headerIndex;
        Columns     = columns;
        Pitch       = pitch;
    }

    /// <summary>Index, in the section's lines, of the month header line.</summary>
    public int HeaderIndex { get; }

    public IReadOnlyList<(string Period, double Center)> Columns { get; }

    public double Pitch { get; }

    /// <summary>
    /// Finds the first month header in <paramref name="lines"/> at or after <paramref name="from"/>.
    /// Null when there is none. Throws <see cref="NosisReportFormatException"/> when a header is there
    /// but its years cannot be placed.
    /// </summary>
    public static NosisReportMonthColumns? Find(IReadOnlyList<NosisReportLine> lines, DateTime reportDate, int from = 0)
    {
        for (var i = from; i < lines.Count; i++)
        {
            var line = lines[i];
            var headerWords = ExpandGluedMonths(line.Words);

            // Self-dated tokens: "09/2026", "Sep-26".
            var dated = headerWords.Select(w => (Word: w, Period: NosisReportPeriods.Parse(w.Text))).ToList();
            if (dated.Count(d => d.Period is not null) >= MinColumns && dated.Count(d => d.Period is not null) * 2 >= dated.Count)
            {
                var columns = dated.Where(d => d.Period is not null).Select(d => (d.Period!, d.Word.Center)).ToList();
                return Build(i, columns);
            }

            // Numeric months under a year line: "12 1 2 … 12 1 … 11" below "2024 2025 2026".
            if (NumericMonths(lines, i) is { } numeric)
            {
                var numericYears = YearsAbove(lines, i);
                var numericPeriods = PlaceYears(numeric.Select(n => n.Month).ToList(), numericYears,
                    numeric.Select(n => (n.Word, (int?)n.Month)).ToList(), reportDate);
                return Build(i, numericPeriods.Select((p, k) => (p, numeric[k].Word.Center)).ToList());
            }

            // Bare months: "Oct Nov Dic Ene …" — the years come from the line above, or the report date.
            var months = headerWords.Select(w => (Word: w, Month: NosisReportPeriods.Month(w.Text))).ToList();
            var monthCount = months.Count(m => m.Month is not null);
            if (monthCount < MinColumns || monthCount * 2 < months.Count)
                continue;

            var monthWords = months.Where(m => m.Month is not null).ToList();
            var years = YearsAbove(lines, i);
            var periods = PlaceYears(monthWords.Select(m => m.Month!.Value).ToList(), years, monthWords, reportDate);
            return Build(i, periods.Select((p, k) => (p, monthWords[k].Word.Center)).ToList());
        }

        return null;
    }

    /// <summary>The row's label (words left of the first column) and its words per column index.</summary>
    /// <exception cref="NosisReportFormatException">A word sits under no column.</exception>
    public (string Label, Dictionary<int, List<NosisReportWord>> Cells) Read(NosisReportLine line)
        => TryRead(line, out var label, out var cells)
            ? (label, cells)
            : throw new NosisReportFormatException("A cell does not sit under any month column.");

    /// <summary>
    /// <see cref="Read"/> without the throw: false when a word after the first cell sits under no column
    /// — a line of another table, a note, a legend — so the caller can skip it.
    /// </summary>
    public bool TryRead(NosisReportLine line, out string label, out Dictionary<int, List<NosisReportWord>> cells)
    {
        var labelWords = new List<NosisReportWord>();
        cells = new Dictionary<int, List<NosisReportWord>>();
        label = string.Empty;
        var firstEdge = Columns[0].Center - (AssignShare * Pitch);
        var labelStartLimit = Columns[0].Center - (Pitch / 2d);

        foreach (var word in line.Words)
        {
            // Left of the columns, or a label word that STARTS before the first column's half-pitch
            // boundary and merely runs into it (a long or wide entity name).
            if (word.Right < firstEdge || (cells.Count == 0 && word.Left < labelStartLimit))
            {
                labelWords.Add(word);
                continue;
            }

            var best = -1;
            var bestDistance = double.MaxValue;
            for (var c = 0; c < Columns.Count; c++)
            {
                var distance = Math.Abs(word.Center - Columns[c].Center);
                if (distance < bestDistance)
                {
                    best = c;
                    bestDistance = distance;
                }
            }

            if (bestDistance > AssignShare * Pitch)
            {
                if (cells.Count == 0)
                {
                    labelWords.Add(word);   // a long label running into the first column
                    continue;
                }

                return false;
            }

            if (!cells.TryGetValue(best, out var list))
                cells[best] = list = [];
            list.Add(word);
        }

        label = string.Join(' ', labelWords.Select(w => w.Text));
        return true;
    }

    /// <summary>
    /// True when line <paramref name="index"/> is a month header (a first one, or the table repeated
    /// after a page break): month names or self-dated tokens, or numeric months under a year line.
    /// </summary>
    public static bool IsHeader(IReadOnlyList<NosisReportLine> lines, int index)
    {
        var words = ExpandGluedMonths(lines[index].Words);
        var monthish = words.Count(w => NosisReportPeriods.Month(w.Text) is not null || NosisReportPeriods.Parse(w.Text) is not null);
        if (monthish >= MinColumns && monthish * 2 >= words.Count)
            return true;

        return NumericMonths(lines, index) is not null;
    }

    /// <summary>
    /// The month numbers of a NUMERIC header: every word an integer 1-12, at least
    /// <see cref="MinNumericColumns"/> of them, stepping by one (either way, wrapping 12↔1), AND a bare
    /// year line within 3 lines above — without it a row of small integers could pass for a header.
    /// </summary>
    private static List<(NosisReportWord Word, int Month)>? NumericMonths(IReadOnlyList<NosisReportLine> lines, int index)
    {
        var words = lines[index].Words;
        if (words.Count < MinNumericColumns)
            return null;

        var months = new List<(NosisReportWord Word, int Month)>(words.Count);
        foreach (var word in words)
        {
            if (!int.TryParse(word.Text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var month)
                || month is < 1 or > 12)
                return null;
            months.Add((word, month));
        }

        var forward  = true;
        var backward = true;
        for (var k = 1; k < months.Count; k++)
        {
            forward  &= months[k].Month == Next(months[k - 1].Month);
            backward &= months[k].Month == Previous(months[k - 1].Month);
        }

        if (!forward && !backward)
            return null;

        return YearsAbove(lines, index).Count > 0 ? months : null;
    }

    /// <summary>
    /// Narrow month columns can print their labels with no gap between them, so the text layer gives
    /// one word "MayJun". A word made only of 3-letter month abbreviations is split back into its
    /// months, each taking an equal share of the word's width.
    /// </summary>
    internal static IReadOnlyList<NosisReportWord> ExpandGluedMonths(IReadOnlyList<NosisReportWord> words)
    {
        if (!words.Any(IsGlued))
            return words;

        var expanded = new List<NosisReportWord>(words.Count + 4);
        foreach (var word in words)
        {
            if (!IsGlued(word))
            {
                expanded.Add(word);
                continue;
            }

            var parts = word.Text.Length / 3;
            var width = (word.Right - word.Left) / parts;
            for (var k = 0; k < parts; k++)
                expanded.Add(new NosisReportWord(word.Text.Substring(k * 3, 3), word.Left + (k * width), word.Left + ((k + 1) * width)));
        }

        return expanded;
    }

    private static bool IsGlued(NosisReportWord word)
    {
        var text = word.Text;
        if (text.Length < 6 || text.Length % 3 != 0 || !text.All(char.IsLetter))
            return false;

        for (var k = 0; k < text.Length; k += 3)
        {
            if (NosisReportPeriods.Month(text.Substring(k, 3)) is null)
                return false;
        }

        return true;
    }

    /// <summary>True when the line is a bare year line ("2024 2025 2026").</summary>
    public static bool IsYearLine(NosisReportLine line)
        => line.Words.Count > 0 && line.Words.All(w => NosisReportPeriods.YearToken(w.Text) is not null);

    private static NosisReportMonthColumns Build(int headerIndex, List<(string Period, double Center)> columns)
    {
        if (columns.Select(c => c.Period).Distinct(StringComparer.Ordinal).Count() != columns.Count)
            throw new NosisReportFormatException("A month column repeats.");

        var centers = columns.Select(c => c.Center).ToList();
        var gaps = centers.Zip(centers.Skip(1), (a, b) => b - a).Where(g => g > 0).OrderBy(g => g).ToList();
        if (gaps.Count == 0)
            throw new NosisReportFormatException("Month columns have no spacing.");

        return new NosisReportMonthColumns(headerIndex, columns, gaps[gaps.Count / 2]);
    }

    private static List<(int Year, double Center)> YearsAbove(IReadOnlyList<NosisReportLine> lines, int headerIndex)
    {
        for (var j = headerIndex - 1; j >= Math.Max(0, headerIndex - 3); j--)
        {
            if (IsYearLine(lines[j]))
                return lines[j].Words.Select(w => (NosisReportPeriods.YearToken(w.Text)!.Value, w.Center)).ToList();
        }

        return [];
    }

    /// <summary>
    /// Years for a run of bare months. The months must step by exactly one (either direction); the year
    /// changes where they wrap. With a year line, the first column takes the year printed nearest to its
    /// left edge of the table and every derived year must be one the line prints. Without one, the
    /// newest column is the latest such month not after the report date.
    /// </summary>
    private static List<string> PlaceYears(
        List<int> months, List<(int Year, double Center)> years,
        List<(NosisReportWord Word, int? Month)> monthWords, DateTime reportDate)
    {
        var step = months.Count > 1 && Next(months[0]) == months[1] ? 1 : -1;
        for (var k = 1; k < months.Count; k++)
        {
            var expected = step == 1 ? Next(months[k - 1]) : Previous(months[k - 1]);
            if (months[k] != expected)
                throw new NosisReportFormatException("Month columns are not consecutive.");
        }

        var offsets = new List<int> { 0 };
        for (var k = 1; k < months.Count; k++)
        {
            var wrapped = step == 1 ? months[k] == 1 : months[k] == 12;
            offsets.Add(offsets[^1] + (wrapped ? step : 0));
        }

        int firstYear;
        if (years.Count > 0)
        {
            var printed = years.Select(y => y.Year).ToHashSet();
            firstYear = YearBySpan(offsets, years, monthWords) ?? YearAtLeft(years, monthWords);

            if (offsets.Any(o => !printed.Contains(firstYear + o)))
                throw new NosisReportFormatException("Month columns disagree with the year line.");
        }
        else
        {
            var newestIndex = step == 1 ? months.Count - 1 : 0;
            var newestMonth = months[newestIndex];
            var newestYear  = newestMonth <= reportDate.Month ? reportDate.Year : reportDate.Year - 1;
            firstYear = newestYear - offsets[newestIndex];
        }

        return months.Select((m, k) => NosisReportPeriods.Format(firstYear + offsets[k], m)).ToList();
    }

    /// <summary>
    /// The first column's year from the year labels' positions: each run of columns within one year
    /// takes the label whose centre lies inside that run's span (half a pitch either side). Every run
    /// that finds one must agree. Null when no run finds one.
    /// </summary>
    private static int? YearBySpan(
        List<int> offsets, List<(int Year, double Center)> years, List<(NosisReportWord Word, int? Month)> monthWords)
    {
        var centers = monthWords.Select(m => m.Word.Center).ToList();
        var gaps = centers.Zip(centers.Skip(1), (a, b) => Math.Abs(b - a)).Where(g => g > 0).OrderBy(g => g).ToList();
        var half = gaps.Count == 0 ? 10d : gaps[gaps.Count / 2] / 2d;

        int? first = null;
        foreach (var run in offsets.Select((o, k) => (Offset: o, Index: k)).GroupBy(x => x.Offset))
        {
            var lo = run.Min(x => centers[x.Index]) - half;
            var hi = run.Max(x => centers[x.Index]) + half;
            var inside = years.Where(y => y.Center >= lo && y.Center <= hi).Select(y => y.Year).Distinct().ToList();
            if (inside.Count != 1)
                continue;

            var candidate = inside[0] - run.Key;
            if (first is not null && first != candidate)
                throw new NosisReportFormatException("The year labels disagree with the month runs.");
            first = candidate;
        }

        return first;
    }

    /// <summary>The year printed at or left of the first column, else the leftmost one.</summary>
    private static int YearAtLeft(List<(int Year, double Center)> years, List<(NosisReportWord Word, int? Month)> monthWords)
    {
        var firstCenter = monthWords[0].Word.Center;
        var ordered = years.OrderBy(y => y.Center).ToList();
        return ordered.LastOrDefault(y => y.Center <= firstCenter + 1d) is { Year: > 0 } atLeft ? atLeft.Year : ordered[0].Year;
    }

    private static int Next(int month) => month == 12 ? 1 : month + 1;

    private static int Previous(int month) => month == 1 ? 12 : month - 1;
}
