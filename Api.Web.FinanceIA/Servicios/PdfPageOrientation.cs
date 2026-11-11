// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Decides, per page, how far a rendered page must be turned for its text to read upright — the
/// case of a balance table drawn rotated 90° inside an unrotated portrait page, which a renderer
/// draws exactly as it is (sideways).
///
/// <para>
/// Reads the direction of the glyphs from the PDF content stream with PdfPig (already referenced
/// for <see cref="PdfAttachmentExtractor"/>). The direction comes from the text matrix and the glyph
/// placement, NOT from the characters, so it is readable from a text layer whose characters are
/// garbage (Type 3 fonts with no ToUnicode map). Local, deterministic and free: nothing leaves the
/// process, and no model is asked.
/// </para>
///
/// <para>
/// A page with no text layer (a scan) yields 0 — rendered as it is, exactly as before. Any parsing
/// failure yields 0 for every page: orientation is an improvement, never a reason to fail a pass.
/// <see cref="DetectPageOrientations"/> also says which pages the glyphs actually DECIDED, so the
/// second pass can ask an image-based detector (<see cref="IScanOrientationDetector"/>) about the rest.
/// </para>
/// </summary>
internal static class PdfPageOrientation
{
    /// <summary>Oriented glyphs a page needs before its dominant direction is trusted.</summary>
    internal const int MinOrientedLetters = 20;

    /// <summary>Share of the oriented glyphs the dominant non-horizontal direction must hold.</summary>
    internal const double DominantShare = 0.60;

    /// <summary>
    /// Clockwise degrees (0, 90, 180 or 270) to turn each of the first <paramref name="maxPages"/>
    /// rendered pages so its text reads upright. Never throws; an unreadable document yields an
    /// all-zero list of the length that could be determined (possibly empty).
    /// </summary>
    /// <param name="onFailure">Receives the exception type name when the document cannot be read.</param>
    public static IReadOnlyList<int> DetectUprightRotations(byte[] pdfBytes, int maxPages, Action<string>? onFailure = null) =>
        DetectPageOrientations(pdfBytes, maxPages, onFailure).Select(p => p.Rotation).ToList();

    /// <summary>
    /// Per page, the clockwise turn <see cref="DetectUprightRotations"/> returns AND whether the glyphs
    /// decided it: <see cref="PageOrientation.Decided"/> is true only when the page carries at least
    /// <see cref="MinOrientedLetters"/> oriented glyphs. A scan (no text layer), a near-empty page or a
    /// page that could not be read is undecided — its 0 means "nothing to go on", not "upright", and an
    /// image-based detector may still judge it. Never throws; an unreadable document yields an undecided
    /// list of the length that could be determined (possibly empty).
    /// </summary>
    /// <param name="onFailure">Receives the exception type name when the document or a page cannot be read.</param>
    public static IReadOnlyList<PageOrientation> DetectPageOrientations(byte[] pdfBytes, int maxPages, Action<string>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);

        var pages = new List<PageOrientation>();
        try
        {
            using var document = PdfDocument.Open(pdfBytes);
            var count = Math.Min(document.NumberOfPages, maxPages);

            for (var number = 1; number <= count; number++)
            {
                try
                {
                    var page = document.GetPage(number);
                    pages.Add(Decide(page.Letters.Select(l => l.TextOrientation)));
                }
                catch (Exception ex)
                {
                    // One unreadable page (an exotic Type 3 glyph procedure, a broken content stream)
                    // costs that page its correction, not the document.
                    onFailure?.Invoke(ex.GetType().Name);
                    pages.Add(PageOrientation.Undecided);
                }
            }
        }
        catch (Exception ex)
        {
            onFailure?.Invoke(ex.GetType().Name);
            return pages.Select(_ => PageOrientation.Undecided).ToList();
        }

        return pages;
    }

    /// <summary>
    /// The clockwise turn that brings the dominant DISPLAYED direction of <paramref name="orientations"/>
    /// to horizontal. PdfPig 0.1.15 maps the page's /Rotate into its initial transformation matrix, so
    /// a letter's orientation is already the one a viewer — and PDFium, which honours /Rotate — shows:
    /// <list type="bullet">
    /// <item><c>Rotate270</c> — text running UP the page: turn the image 90° clockwise.</item>
    /// <item><c>Rotate90</c>  — text running DOWN the page: turn it 270° clockwise (90° counter-clockwise).</item>
    /// <item><c>Rotate180</c> — upside down: 180°.</item>
    /// </list>
    /// PdfPageOrientationTests pins both halves of this (a rotated text matrix on an unrotated page,
    /// and horizontal text on a /Rotate 90 page).
    /// </summary>
    internal static int UprightRotation(IEnumerable<TextOrientation> orientations) => Decide(orientations).Rotation;

    private static PageOrientation Decide(IEnumerable<TextOrientation> orientations)
    {
        int horizontal = 0, rotate90 = 0, rotate180 = 0, rotate270 = 0;
        foreach (var orientation in orientations)
        {
            switch (orientation)
            {
                case TextOrientation.Horizontal: horizontal++; break;
                case TextOrientation.Rotate90:   rotate90++;   break;
                case TextOrientation.Rotate180:  rotate180++;  break;
                case TextOrientation.Rotate270:  rotate270++;  break;
            }
        }

        var oriented = horizontal + rotate90 + rotate180 + rotate270;
        if (oriented < MinOrientedLetters)
            return PageOrientation.Undecided;

        var (count, turn) = new[] { (rotate270, 90), (rotate180, 180), (rotate90, 270) }
            .OrderByDescending(c => c.Item1)
            .First();

        return new PageOrientation(count > horizontal && count >= DominantShare * oriented ? turn : 0, Decided: true);
    }
}

