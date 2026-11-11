// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// Renders the first pages of a PDF as JPEG images for the classification's image read mode.
/// A seam so the classification can be tested without the native PDFium renderer.
/// </summary>
public interface ILegajoPageRenderer
{
    /// <summary>
    /// Renders the first <paramref name="maxPages"/> pages at <paramref name="dpi"/> and returns them
    /// as JPEG bytes in page order. Throws when the PDF cannot be rendered. A page whose size at that
    /// DPI would exceed the classification's cap is rendered straight at the cap instead.
    /// </summary>
    IReadOnlyList<byte[]> RenderJpegs(byte[] pdfBytes, int dpi, int maxPages);

    /// <summary>
    /// As <see cref="RenderJpegs(byte[], int, int)"/>, but every page whose longer side would exceed
    /// <paramref name="maxLongEdgePx"/> at that DPI is rendered directly at that length, keeping the
    /// aspect ratio — never at full size first (Phase 2b's
    /// extraction: the model reads at most a 1568 px long edge, and a request with more than 20 images
    /// rejects any image over 2000 px).
    /// </summary>
    IReadOnlyList<byte[]> RenderJpegs(byte[] pdfBytes, int dpi, int maxPages, int maxLongEdgePx);
}
