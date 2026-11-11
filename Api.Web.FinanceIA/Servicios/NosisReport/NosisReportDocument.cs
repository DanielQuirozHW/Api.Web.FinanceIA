// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// The report's text layer, page by page, as lines of positioned words — and what the page furniture
/// says: the title's CUIT, each footer's CUIT and date. The title and footer lines are taken OUT of the
/// line stream (the title carries the business name, the footer the CUIT), so no section ever sees them.
/// </summary>
internal sealed class NosisReportDocument
{
    /// <summary>Share of a font size two words' baselines may differ by and still be one line.</summary>
    private const double LineBaselineShare = 0.35;

    private static readonly Regex TitleCuitPattern = new(
        @"INFORME\s+INDIVIDUAL\s*>\s*(\d{2}-?\d{8}-?\d)", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    private static readonly Regex FooterCuitPattern = new(
        @"DOC\.?\s*(\d{2}-?\d{8}-?\d)", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    private static readonly Regex FooterDatePattern = new(
        @"(\d{2}\.\d{2}\.\d{4})\s+(\d{2}:\d{2})", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    private static readonly Regex PageNumber = new(
        @"^(PAGINA|PAG\.?|HOJA)\s*\d+(\s*(DE|/)\s*\d+)?$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    private NosisReportDocument(
        int pageCount, IReadOnlyList<NosisReportLine> lines, string? titleCuit, bool firstPageHasTitle,
        bool firstPageHasFooter, IReadOnlyList<string> footerCuits, DateTime? reportDate)
    {
        PageCount          = pageCount;
        Lines              = lines;
        TitleCuit          = titleCuit;
        FirstPageHasTitle  = firstPageHasTitle;
        FirstPageHasFooter = firstPageHasFooter;
        FooterCuits        = footerCuits;
        ReportDate         = reportDate;
    }

    public int PageCount { get; }

    /// <summary>The body lines of every page, top to bottom, page after page; furniture removed.</summary>
    public IReadOnlyList<NosisReportLine> Lines { get; }

    /// <summary>The CUIT digits of the "Informe Individual > CUIT" title (page 1). Never logged or stored.</summary>
    public string? TitleCuit { get; }

    public bool FirstPageHasTitle { get; }

    public bool FirstPageHasFooter { get; }

    /// <summary>The distinct CUIT digits the footers name. Never logged or stored.</summary>
    public IReadOnlyList<string> FooterCuits { get; }

    /// <summary>Page 1's footer date and time (Argentina wall-clock, Kind Unspecified).</summary>
    public DateTime? ReportDate { get; }

    /// <summary>The number of pages, read without the text layer (for the page cap).</summary>
    public static int CountPages(PdfDocument document) => document.NumberOfPages;

    /// <summary>Reads every page of an opened document. Cancellation is checked per page.</summary>
    public static NosisReportDocument Read(PdfDocument document, CancellationToken ct)
    {
        var lines = new List<NosisReportLine>();
        var footerCuits = new HashSet<string>(StringComparer.Ordinal);
        string? titleCuit = null;
        DateTime? reportDate = null;
        var firstPageHasTitle  = false;
        var firstPageHasFooter = false;

        for (var number = 1; number <= document.NumberOfPages; number++)
        {
            ct.ThrowIfCancellationRequested();

            var page = document.GetPage(number);
            var pageLines = ReadLines(page, number);

            for (var i = 0; i < pageLines.Count; i++)
            {
                var line = pageLines[i];

                if (line.Normalised.Contains("NOSIS.MANAGER", StringComparison.Ordinal))
                {
                    // The footer may wrap: read it with the line below it.
                    var footer = i + 1 < pageLines.Count ? line.Normalised + " " + pageLines[i + 1].Normalised : line.Normalised;

                    var cuit = FooterCuitPattern.Match(footer);
                    if (cuit.Success)
                        footerCuits.Add(Digits(cuit.Groups[1].Value));

                    if (number == 1)
                    {
                        firstPageHasFooter = true;
                        var date = FooterDatePattern.Match(footer);
                        if (date.Success && DateTime.TryParseExact(
                                date.Groups[1].Value + " " + date.Groups[2].Value, "dd.MM.yyyy HH:mm",
                                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                        {
                            reportDate = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
                        }
                    }

                    // The wrapped half of the footer goes too, when it is the Doc./date half.
                    if (i + 1 < pageLines.Count && IsFooterTail(pageLines[i + 1]))
                        i++;
                    continue;
                }

                var title = TitleCuitPattern.Match(line.Normalised);
                if (title.Success)
                {
                    if (number == 1)
                    {
                        firstPageHasTitle = true;
                        titleCuit ??= Digits(title.Groups[1].Value);
                    }

                    continue;   // the title carries the business name: never a section line
                }

                if (PageNumber.IsMatch(line.Normalised) || IsFooterTail(line))
                    continue;

                lines.Add(line);
            }
        }

        return new NosisReportDocument(
            document.NumberOfPages, lines, titleCuit, firstPageHasTitle, firstPageHasFooter,
            footerCuits.ToList(), reportDate);
    }

    private static bool IsFooterTail(NosisReportLine line)
        => line.Normalised.Contains("INFORMACION CONFIDENCIAL", StringComparison.Ordinal)
           || (FooterCuitPattern.IsMatch(line.Normalised) && line.Normalised.Contains("CLI.", StringComparison.Ordinal));

    private static List<NosisReportLine> ReadLines(Page page, int number)
    {
        var words = new List<(NosisReportWord Word, double Baseline, double Size, IReadOnlyList<Letter> Letters)>();
        foreach (var word in page.GetWords())
        {
            if (string.IsNullOrWhiteSpace(word.Text) || word.Letters.Count == 0)
                continue;

            var first = word.Letters[0];
            var size  = word.Letters.Max(l => l.PointSize);
            if (!(size > 0))
                size = Math.Max(word.BoundingBox.Height, 1d);

            words.Add((new NosisReportWord(word.Text, word.BoundingBox.Left, word.BoundingBox.Right), first.StartBaseLine.Y, size, word.Letters));
        }

        // Top of the page first (PDF y grows upwards), then left to right.
        var rows = new List<(double Baseline, double Size, List<NosisReportWord> Words, List<Letter> Letters)>();
        foreach (var item in words.OrderByDescending(w => w.Baseline).ThenBy(w => w.Word.Left))
        {
            var row = rows.Count > 0 ? rows[^1] : default;
            if (rows.Count == 0 || Math.Abs(item.Baseline - row.Baseline) > LineBaselineShare * Math.Max(item.Size, row.Size))
            {
                rows.Add((item.Baseline, item.Size, [item.Word], [.. item.Letters]));
            }
            else
            {
                row.Words.Add(item.Word);
                row.Letters.AddRange(item.Letters);
            }
        }

        return rows
            .Select(r => new NosisReportLine(number, r.Baseline, r.Words.OrderBy(w => w.Left).ToList(), StyleOf(r.Letters)))
            .ToList();
    }

    /// <summary>
    /// The line's dominant point size (0.5 pt steps), whether most of its letters are bold, and the
    /// colour most of them are filled with. Never throws: a property PdfPig cannot read is "unknown".
    /// </summary>
    private static NosisReportLineStyle StyleOf(IReadOnlyList<Letter> letters)
    {
        var visible = letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
        if (visible.Count == 0)
            return NosisReportLineStyle.Unknown;

        var size = visible
            .GroupBy(l => Math.Round(l.PointSize * 2d, MidpointRounding.AwayFromZero) / 2d)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key)
            .First().Key;

        var bold = visible.Count(IsBold) * 2 > visible.Count;

        var color = visible
            .Select(ColorOf)
            .GroupBy(c => c)
            .OrderByDescending(g => g.Count())
            .First().Key;

        return new NosisReportLineStyle(size, bold, color);
    }

    private static bool IsBold(Letter letter)
    {
        try
        {
            var details = letter.FontDetails;
            if (details is not null && (details.IsBold || details.Weight >= 700))
                return true;

            var name = letter.FontName ?? details?.Name ?? string.Empty;
            return name.Contains("Bold", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("Black", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("Heavy", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? ColorOf(Letter letter)
    {
        try
        {
            if (letter.FillColor is null)
                return null;

            var (r, g, b) = letter.FillColor.ToRGBValues();
            return string.Create(CultureInfo.InvariantCulture,
                $"#{(int)Math.Round(r * 255):x2}{(int)Math.Round(g * 255):x2}{(int)Math.Round(b * 255):x2}");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Digits(string cuit) => new(cuit.Where(char.IsAsciiDigit).ToArray());
}
