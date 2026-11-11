// Project : Api.Web.FinanceIA
// Created : 2026-11-09

using iText.Kernel.Font;
using iText.Layout.Element;
using iText.Layout.Properties;
using iText.Layout.Renderer;

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// How far a label set smaller (or in another face) must move down to sit on the baseline of the figures of its
/// row. A top-aligned cell puts its first baseline a fixed distance below the cell's content top, and that
/// distance depends on the font and the size: under a multiplied leading L, iText's line layout adds half the
/// extra leading of the line box above it, so the first baseline sits ascent + (ascent − descent) × (L − 1) / 2
/// below the top (descent negative; LineRenderer's top leading indent). A figure cell and a label cell with the
/// same top padding put their baselines apart by the difference of those distances — 3.0 pt between a 9 pt
/// JetBrains Mono figure and an 8 pt DM Sans Medium label. The metrics are the ones iText's line layout reads
/// (<see cref="TextRenderer.CalculateAscenderDescender(PdfFont)"/>, and the paragraph's default leading), never a
/// glyph's ink.
/// </summary>
internal static class PdfBaseline
{
    // The leading every report paragraph uses: iText's default (multiplied), read rather than restated.
    private static readonly float LeadingMultiplier =
        new Paragraph().GetDefaultProperty<Leading>(Property.LEADING) is { } leading && leading.GetLeadingType() == Leading.MULTIPLIED
            ? leading.GetValue()
            : 1f;

    /// <summary>
    /// The extra top padding (pt) that puts a <paramref name="labelSize"/> pt <paramref name="label"/> line on the
    /// baseline of a <paramref name="figureSize"/> pt <paramref name="figure"/> line laid out under the same padding.
    /// </summary>
    internal static float Shift(PdfFont figure, float figureSize, PdfFont label, float labelSize)
    {
        ArgumentNullException.ThrowIfNull(figure);
        ArgumentNullException.ThrowIfNull(label);

        return FirstBaseline(figure, figureSize) - FirstBaseline(label, labelSize);
    }

    /// <summary>Distance (pt) from a paragraph's top to its first baseline, in the default leading.</summary>
    internal static float FirstBaseline(PdfFont font, float size)
    {
        float[] ascenderDescender = TextRenderer.CalculateAscenderDescender(font);
        float ascent  = ascenderDescender[0] * size / 1000f;
        float descent = ascenderDescender[1] * size / 1000f;
        return ascent + ((ascent - descent) * (LeadingMultiplier - 1f) / 2f);
    }
}
