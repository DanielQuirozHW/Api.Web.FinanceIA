// Project : Api.Web.FinanceIA
// Created : 2026-11-09

using iText.Kernel.Geom;

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// Page 1's Financia lockup — symbol + wordmark — placed by the Portal's <c>FLogo</c> rule (packages/ui/src/brand/
/// FLogo.tsx, Estratos v1.3): the symbol's INK, not its padded square, is centred on the wordmark's cap-height
/// midline, and the visible gap from the ink's right edge to the wordmark's first stroke is half a cap height. Here
/// the ink's left edge also sits on the content margin, and the shared midline is put on the centre of the
/// right-hand title/date block. A pure function, so it is tested without parsing a PDF.
/// </summary>
internal static class HeaderLockup
{
    /// <summary>The visible symbol → wordmark gap, in cap heights (FLogo's GAP_IN_CAPS).</summary>
    internal const float GapInCaps = 0.5f;

    /// <summary>Where the symbol's square (bottom-left corner) and the wordmark's origin (on its baseline) land.</summary>
    internal readonly record struct Placement(float SymbolX, float SymbolY, float WordmarkX, float WordmarkBaseline);

    /// <summary>
    /// Lays the lockup out from <paramref name="left"/> (the content margin) on <paramref name="midline"/>.
    /// </summary>
    /// <param name="symbolInk">The inked part of the symbol's square, relative to its bottom-left corner.</param>
    /// <param name="capHeight">The wordmark face's cap height at the wordmark's size, in points.</param>
    /// <param name="firstLeftBearing">
    /// The left side bearing of the wordmark's first glyph at its size, in points: the glyph's ink starts that far right
    /// of the text origin, so the gap is measured to the ink, not to the origin.
    /// </param>
    internal static Placement Layout(float left, float midline, Rectangle symbolInk, float capHeight, float firstLeftBearing)
    {
        ArgumentNullException.ThrowIfNull(symbolInk);

        float symbolX = left - symbolInk.GetLeft();
        float symbolY = midline - (symbolInk.GetBottom() + (symbolInk.GetHeight() / 2f));

        float inkRight  = left + symbolInk.GetWidth();
        float wordmarkX = inkRight + (GapInCaps * capHeight) - firstLeftBearing;
        float baseline  = midline - (capHeight / 2f);

        return new Placement(symbolX, symbolY, wordmarkX, baseline);
    }

    /// <summary>
    /// The vertical centre of a block of lines, from the first line's cap top to the last line's baseline — the
    /// optical extent of text set without descenders below the last line.
    /// </summary>
    internal static float BlockCentre(float firstBaseline, float firstCapHeight, float lastBaseline) =>
        (firstBaseline + firstCapHeight + lastBaseline) / 2f;
}
