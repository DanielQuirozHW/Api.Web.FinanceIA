// Project : Api.Web.FinanceIA
// Created : 2026-11-08

using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf.Canvas;

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// The Financia brand as the reports draw it: a symbol and a wordmark in page 1's header lockup, and the name in the
/// footer and the document metadata. The page furniture draws ONLY through this. Only
/// <see cref="FinanciaPdfBrand"/> ships; a client's logo is <see cref="ClientPdfBrand"/>, which takes the header slot
/// only.
/// </summary>
public interface IPdfBrand
{
    /// <summary>The wordmark text, set in the heading face in one ink.</summary>
    string Wordmark { get; }

    /// <summary>The name the document metadata and the closing block use ("Financia").</summary>
    string ProductName { get; }

    /// <summary>Draws the symbol in a <paramref name="size"/>-point square whose bottom-left corner is (x, y).</summary>
    void DrawSymbol(PdfCanvas canvas, float x, float y, float size, Color color);

    /// <summary>
    /// The part of the <paramref name="size"/>-point square that <see cref="DrawSymbol"/> actually inks, relative to
    /// its bottom-left corner (the ring's stroke included) — so a caller aligns the visible mark, not its padded
    /// square, with the margin and the wordmark.
    /// </summary>
    Rectangle SymbolInk(float size);
}

/// <summary>
/// The Financia mark, Estratos v1.3 — a port of the Portal's <c>FSymbol</c> (packages/ui/src/brand/FSymbol.tsx):
/// three 15×2.5 rounded bars stepping 1.5u left as they descend, under a ring (r 2.5, stroke 1.5) with a dot
/// (r 0.85), on the 24-unit brand grid. Flat, one ink, no gradient. Only the full version is drawn: the PDF draws
/// the symbol in page 1's header alone, at 26 pt. The manual's reduced version (two bars, thicker ring) exists for
/// sizes under 24 pt, and nothing in the PDF is drawn that small any more — the footer carries the name only, since
/// any mark it could fit sat below the manual's minimum size.
/// </summary>
public sealed class FinanciaPdfBrand : IPdfBrand
{
    private const float Grid = 24f;

    /// <summary>The one instance; the brand is stateless.</summary>
    public static FinanciaPdfBrand Instance { get; } = new();

    /// <inheritdoc />
    public string Wordmark => "Financia";

    /// <inheritdoc />
    public string ProductName => "Financia";

    /// <inheritdoc />
    /// <remarks>
    /// On the 24-unit grid the ink runs from x 3 (the lowest bar) to x 21 (the right end of the bars), and from
    /// y 1 (the lowest bar's bottom, SVG y 23) up to the ring's outer edge: SVG y 8.25 − r 2.5 − half the 1.5
    /// stroke, so 19 — the Portal's FLogo ink box (x 3→21, SVG y 5→23).
    /// </remarks>
    public Rectangle SymbolInk(float size)
    {
        float s = size / Grid;
        return new Rectangle(3f * s, 1f * s, 18f * s, 18f * s);
    }

    /// <inheritdoc />
    public void DrawSymbol(PdfCanvas canvas, float x, float y, float size, Color color)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(color);

        float s = size / Grid;

        // SVG space is y-down; the canvas is y-up. A rect at (rx, ry) of height h lands at Grid - ry - h.
        void Bar(float rx, float ry) =>
            canvas.RoundRectangle(x + (rx * s), y + ((Grid - ry - 2.5f) * s), 15f * s, 2.5f * s, 1.25f * s);

        canvas.SaveState().SetFillColor(color).SetStrokeColor(color);

        Bar(6f, 13f);
        Bar(4.5f, 16.75f);
        Bar(3f, 20.5f);
        canvas.Fill();

        float cx = x + (13.5f * s);
        float cy = y + ((Grid - 8.25f) * s);

        // The ring is CLOSED before it is stroked (s, not S): an open circle ends where it starts, at 3 o'clock, with
        // two butt caps that leave a visible notch in the stroke.
        canvas.SetLineWidth(1.5f * s).Circle(cx, cy, 2.5f * s).ClosePathStroke();
        canvas.Circle(cx, cy, 0.85f * s).Fill();

        canvas.RestoreState();
    }
}
