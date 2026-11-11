// Project : Api.Web.FinanceIA
// Created : 2026-11-09

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Models.Financia;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace Api.Web.FinanceIA.Servicios.BalanceCorrections;

/// <summary>
/// Finds a suggested figure where it is PRINTED, in the page's text layer (PdfPig: local, deterministic, no
/// model call, no cost), so the evidence no longer depends on the model's estimated row band.
/// </summary>
/// <remarks>
/// <para>
/// Each page is read once, lazily, and turned into rows of upright glyphs in the frame of the image the Portal
/// shows: the full page as rendered for the suggestion, turned by the SAME glyph decision the renderer's plan
/// uses (<see cref="PdfPageOrientation"/>; PdfPig 0.1.15 already folds /Rotate into letter coordinates, as PDFium
/// honours it when rendering). A half split does not change that frame. A page whose glyphs do not decide its
/// orientation (a scan, a near-empty page — the renderer then asked the image detector, which cannot be
/// reproduced here), whose upright size does not match the image actually sent, or that cannot be read gets no
/// match — never an error.
/// </para>
/// <para>
/// A figure matches by MAGNITUDE in printed units (es-AR: <c>1.234.567,89</c>, <c>1.234.567</c>, <c>1234567</c>,
/// with or without <c>$</c>, negative in parentheses or with a minus), the row rebuilt from its glyphs so a
/// figure written as several text runs is still one token. Only an UNAMBIGUOUS match counts: the only candidate
/// on the page, or the only one on the row(s) carrying the figure's label.
/// </para>
/// <para>Never logs a figure, a label or any text of the document; holds nothing beyond one request.</para>
/// </remarks>
public sealed class BalanceSuggestionPageText : IDisposable
{
    /// <summary>Largest relative difference between the upright page's aspect and the sent image's.</summary>
    public const double AspectTolerance = 0.02;

    /// <summary>Largest difference, in printed units, between a printed figure and the target.</summary>
    public const decimal ValueTolerance = 0.005m;

    // Two glyphs are on the same row when their baselines are this share of the font height apart at most.
    private const double RowBaselineShare = 0.35;

    // A gap between two glyphs' advances wider than this share of the font height is a word break.
    private const double WordGapShare = 0.15;

    // Shortest normalised label (letters and digits) worth looking for.
    private const int MinLabelLength = 3;

    // An es-AR amount: optional '(' / sign / '$', thousands with '.', decimals with ','. Not glued to another
    // letter, digit or a separator followed by a digit.
    private static readonly Regex AmountPattern = new(
        @"(?<![\p{L}\p{N}.,])\(?\s?[-−–]?\s?(?:\$\s?)?[-−–]?\s?" +
        @"(?<int>\d{1,3}(?:\.\d{3})+|\d+)(?:,(?<dec>\d+))?\)?(?![\p{L}\p{N}]|[.,]\d)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private readonly byte[]              _pdfBytes;
    private readonly Action<string>?     _onFailure;
    private readonly Dictionary<int, PageLayout?> _pages = [];
    private PdfDocument? _document;
    private bool         _openFailed;

    /// <summary>Reads <paramref name="pdfBytes"/> lazily: nothing is parsed until the first <see cref="Find"/>.</summary>
    /// <param name="pdfBytes">The document exactly as the suggestion's renderer read it (unwrapped).</param>
    /// <param name="onFailure">Receives the exception TYPE name when the document or a page cannot be read.</param>
    public BalanceSuggestionPageText(byte[] pdfBytes, Action<string>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        _pdfBytes  = pdfBytes;
        _onFailure = onFailure;
    }

    /// <summary>Searches made (figures looked for on a page).</summary>
    public int Searched { get; private set; }

    /// <summary>Searches that found the figure unambiguously.</summary>
    public int Found { get; private set; }

    /// <summary>
    /// Where <paramref name="printedValue"/> is printed on <paramref name="page"/> (1-based), as fractions of the
    /// upright full page <paramref name="fullImage"/> shows; null when the page has no usable text layer, its
    /// upright size does not match <paramref name="fullImage"/>, the figure is zero or not finite, or it is not
    /// printed there unambiguously.
    /// </summary>
    /// <param name="printedValue">The figure in PRINTED units (pesos ÷ amounts_scale); its sign is ignored.</param>
    /// <param name="label">The figure's label as printed, when known: breaks a tie between equal figures.</param>
    /// <param name="fullImage">The page's full upright image as the request carried it.</param>
    public BalanceSuggestionTextMatch? Find(int page, double printedValue, string? label, RenderedImage? fullImage)
    {
        if (page < 1 || fullImage is null || fullImage.Width <= 0 || fullImage.Height <= 0
            || !double.IsFinite(printedValue) || printedValue == 0d)
        {
            return null;
        }

        decimal target;
        try
        {
            target = Math.Round((decimal)Math.Abs(printedValue), 4);
        }
        catch (OverflowException)
        {
            return null;
        }

        Searched++;
        if (Layout(page) is not { } layout || !SameAspect(layout, fullImage))
            return null;

        var candidates = new List<(int Row, Box Box)>();
        for (var r = 0; r < layout.Rows.Count; r++)
        {
            foreach (var box in layout.Rows[r].Amounts(target))
                candidates.Add((r, box));
        }

        var chosen = candidates.Count == 1 ? candidates[0].Box : (Box?)null;
        if (chosen is null && candidates.Count > 1 && Normalise(label) is { Length: >= MinLabelLength } wanted)
        {
            var onLabelRow = candidates.Where(c => layout.Rows[c.Row].Carries(wanted)).ToList();
            if (onLabelRow.Count == 1)
                chosen = onLabelRow[0].Box;
        }

        if (chosen is not { } b)
            return null;

        Found++;
        return new BalanceSuggestionTextMatch(
            page,
            Clamp(b.Top / layout.Height),
            Clamp(b.Bottom / layout.Height),
            Clamp(b.Left / layout.Width),
            Clamp(b.Right / layout.Width));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _document?.Dispose();
        _document = null;
    }

    // ── The page, read once ────────────────────────────────────────────────────

    private PageLayout? Layout(int page)
    {
        if (_pages.TryGetValue(page, out var cached))
            return cached;

        PageLayout? layout = null;
        if (Document() is { } document && page <= document.NumberOfPages)
        {
            try
            {
                layout = Read(document.GetPage(page));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _onFailure?.Invoke(ex.GetType().Name);
            }
        }

        _pages[page] = layout;
        return layout;
    }

    private PdfDocument? Document()
    {
        if (_document is not null || _openFailed)
            return _document;

        try
        {
            _document = PdfDocument.Open(_pdfBytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _openFailed = true;
            _onFailure?.Invoke(ex.GetType().Name);
        }

        return _document;
    }

    /// <summary>
    /// The page's glyphs in the upright frame, grouped into rows; null when its glyphs do not decide its
    /// orientation (exactly <see cref="PdfPageOrientation"/>'s rule) or it has no usable size.
    /// </summary>
    private static PageLayout? Read(Page page)
    {
        var letters = page.Letters;
        var oriented = letters.Count(l => l.TextOrientation is TextOrientation.Horizontal or TextOrientation.Rotate90
                                                           or TextOrientation.Rotate180 or TextOrientation.Rotate270);
        if (oriented < PdfPageOrientation.MinOrientedLetters)
            return null;   // undecided: the renderer's turn for this page is not reproducible here

        var rotation = PdfPageOrientation.UprightRotation(letters.Select(l => l.TextOrientation));
        var (pageWidth, pageHeight) = (page.Width, page.Height);
        if (!(pageWidth > 0) || !(pageHeight > 0))
            return null;

        var swaps  = rotation is 90 or 270;
        var frame  = new Frame(pageWidth, pageHeight, rotation);
        var upright = rotation switch
        {
            90  => TextOrientation.Rotate270,
            180 => TextOrientation.Rotate180,
            270 => TextOrientation.Rotate90,
            _   => TextOrientation.Horizontal,
        };

        var glyphs = new List<Glyph>();
        foreach (var letter in letters)
        {
            if (letter.TextOrientation != upright || string.IsNullOrEmpty(letter.Value))
                continue;

            var start = frame.Map(letter.StartBaseLine);
            var end   = frame.Map(letter.EndBaseLine);
            var ink   = frame.Map(letter.BoundingBox);
            var size  = letter.PointSize > 0 ? letter.PointSize : Math.Max(ink.Bottom - ink.Top, 0d) / 0.7;
            if (!(size > 0) || !double.IsFinite(start.X) || !double.IsFinite(start.Y))
                continue;

            glyphs.Add(new Glyph(letter.Value, start.X, Math.Max(start.X, end.X), start.Y, size, ink));
        }

        var rows = new List<Row>();
        foreach (var glyph in glyphs.OrderBy(g => g.Baseline).ThenBy(g => g.Start))
        {
            var row = rows.Count > 0 ? rows[^1] : null;
            if (row is null || Math.Abs(glyph.Baseline - row.Baseline) > RowBaselineShare * Math.Max(glyph.Size, row.Size))
                rows.Add(row = new Row(glyph.Baseline, glyph.Size));

            row.Glyphs.Add(glyph);
        }

        foreach (var row in rows)
            row.Build();

        return new PageLayout(swaps ? pageHeight : pageWidth, swaps ? pageWidth : pageHeight, rows);
    }

    private static bool SameAspect(PageLayout layout, RenderedImage image)
    {
        var expected = layout.Width / layout.Height;
        var actual   = (double)image.Width / image.Height;
        return Math.Abs(actual - expected) <= AspectTolerance * expected;
    }

    private static double Clamp(double value) => Math.Clamp(value, 0d, 1d);

    /// <summary>No accents, lower case, every run of non letters/digits one space, trimmed; null when empty.</summary>
    internal static string? Normalise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var builder = new StringBuilder(text.Length);
        var space   = false;
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(c))
            {
                if (space && builder.Length > 0)
                    builder.Append(' ');

                builder.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else
            {
                space = true;
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>The magnitude an <see cref="AmountPattern"/> match stands for; null when it cannot be parsed.</summary>
    internal static decimal? ParseAmount(string integerPart, string? decimals)
    {
        var digits = integerPart.Replace(".", string.Empty, StringComparison.Ordinal);
        var text   = string.IsNullOrEmpty(decimals) ? digits : digits + "." + decimals;
        return decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    // ── Geometry ───────────────────────────────────────────────────────────────

    /// <summary>A rectangle in upright page points, top-left origin.</summary>
    private readonly record struct Box(double Left, double Top, double Right, double Bottom)
    {
        public Box Union(Box other) => new(
            Math.Min(Left, other.Left), Math.Min(Top, other.Top),
            Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
    }

    /// <summary>
    /// PDF user space as PdfPig reports it (bottom-left origin, /Rotate already applied) → the upright frame:
    /// y flipped to a top-left origin, then turned <c>Rotation</c> degrees clockwise exactly as the renderer turns
    /// the bitmap (a point (x, y) of a W × H image goes to (H − y, x) at 90°).
    /// </summary>
    private readonly record struct Frame(double Width, double Height, int Rotation)
    {
        public (double X, double Y) Map(PdfPoint point)
        {
            var x = point.X;
            var y = Height - point.Y;
            return Rotation switch
            {
                90  => (Height - y, x),
                180 => (Width - x, Height - y),
                270 => (y, Width - x),
                _   => (x, y),
            };
        }

        public Box Map(PdfRectangle rectangle)
        {
            var corners = new[] { rectangle.BottomLeft, rectangle.BottomRight, rectangle.TopLeft, rectangle.TopRight }
                .Select(Map)
                .ToList();
            return new Box(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y));
        }
    }

    private sealed record Glyph(string Value, double Start, double End, double Baseline, double Size, Box Ink);

    private sealed record PageLayout(double Width, double Height, IReadOnlyList<Row> Rows);

    /// <summary>One printed row: its glyphs left to right, its text, and which glyph each character came from.</summary>
    private sealed class Row(double baseline, double size)
    {
        private readonly List<int> _owners = [];
        private string  _text       = string.Empty;
        private string? _normalised;

        public double      Baseline { get; } = baseline;
        public double      Size     { get; } = size;
        public List<Glyph> Glyphs   { get; } = [];

        public void Build()
        {
            Glyphs.Sort((a, b) => a.Start.CompareTo(b.Start));

            var builder = new StringBuilder();
            Glyph? previous = null;
            for (var i = 0; i < Glyphs.Count; i++)
            {
                var glyph = Glyphs[i];
                if (previous is not null && glyph.Start - previous.End > WordGapShare * Math.Max(glyph.Size, previous.Size))
                {
                    builder.Append(' ');
                    _owners.Add(-1);
                }

                foreach (var c in glyph.Value)
                {
                    builder.Append(char.IsWhiteSpace(c) ? ' ' : c);
                    _owners.Add(char.IsWhiteSpace(c) ? -1 : i);
                }

                previous = glyph;
            }

            _text       = builder.ToString();
            _normalised = Normalise(_text);
        }

        /// <summary>True when the row's text holds <paramref name="label"/> (normalised) as whole words.</summary>
        public bool Carries(string label) =>
            _normalised is not null
            && (" " + _normalised + " ").Contains(" " + label + " ", StringComparison.Ordinal);

        /// <summary>The ink box of every amount on the row whose magnitude is <paramref name="target"/>.</summary>
        public IEnumerable<Box> Amounts(decimal target)
        {
            if (_text.Length == 0)
                yield break;

            MatchCollection matches;
            try
            {
                matches = AmountPattern.Matches(_text);
                _ = matches.Count;   // evaluate now, inside the timeout guard
            }
            catch (RegexMatchTimeoutException)
            {
                yield break;
            }

            foreach (Match match in matches)
            {
                var value = ParseAmount(match.Groups["int"].Value, match.Groups["dec"].Success ? match.Groups["dec"].Value : null);
                if (value is not { } v || Math.Abs(v - target) > ValueTolerance)
                    continue;

                Box? box = null;
                for (var i = match.Index; i < match.Index + match.Length; i++)
                {
                    if (_owners[i] < 0)
                        continue;

                    var ink = Glyphs[_owners[i]].Ink;
                    box = box is { } b ? b.Union(ink) : ink;
                }

                if (box is { } found)
                    yield return found;
            }
        }
    }
}
