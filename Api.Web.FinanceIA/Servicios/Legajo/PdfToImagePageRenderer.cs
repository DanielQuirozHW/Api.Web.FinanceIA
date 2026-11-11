// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Drawing;
using PDFtoImage;
using SkiaSharp;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// <see cref="ILegajoPageRenderer"/> on PDFtoImage (PDFium), the renderer ClaudeService's image
/// fallback already uses. Stateless; registered as a singleton.
///
/// <para>
/// MEMORY: every page is rendered ON ITS OWN, directly at its target size — never at full DPI first.
/// The page size is read first (<see cref="Conversion.GetPageSizes(byte[], string)"/>, in points). A
/// page whose natural size at the requested DPI fits the long-edge cap is rendered with that DPI exactly
/// as before; a larger one (up to the 14400 pt PDF maximum, or a degenerate strip) is rendered straight
/// at the capped width and height, keeping its aspect ratio. So no bitmap is ever larger than the cap
/// squared, whatever the file declares.
/// </para>
/// </summary>
public sealed class PdfToImagePageRenderer : ILegajoPageRenderer
{
    /// <summary>
    /// The classification's long-edge cap. At its 100 DPI every normal page (up to about 15.7 inches)
    /// renders on the unchanged DPI path, so its images are byte-identical to Phase 2a's; only an
    /// oversized page is capped.
    /// </summary>
    public const int ClassificationMaxLongEdgePx = 1568;

    private const int JpegQuality = 80;
    private const float PointsPerInch = 72f;

    /// <inheritdoc />
    public IReadOnlyList<byte[]> RenderJpegs(byte[] pdfBytes, int dpi, int maxPages)
        => Render(pdfBytes, dpi, maxPages, ClassificationMaxLongEdgePx);

    /// <inheritdoc />
    public IReadOnlyList<byte[]> RenderJpegs(byte[] pdfBytes, int dpi, int maxPages, int maxLongEdgePx)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLongEdgePx, 1);
        return Render(pdfBytes, dpi, maxPages, maxLongEdgePx);
    }

    /// <summary>
    /// How one page of <paramref name="pagePoints"/> is rendered: DPI alone when its natural size fits
    /// <paramref name="maxLongEdgePx"/>; otherwise an explicit width and height, aspect kept, long edge at
    /// the cap and each side at least 1 px. A page without a positive, finite size cannot be rendered.
    /// Internal for the tests.
    /// </summary>
    internal static RenderOptions OptionsFor(SizeF pagePoints, int dpi, int maxLongEdgePx)
    {
        var width  = pagePoints.Width;
        var height = pagePoints.Height;
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0f || height <= 0f)
            throw new InvalidOperationException("The PDF page has no usable size.");

        var naturalLongEdge = Math.Max(width, height) / PointsPerInch * dpi;
        if (naturalLongEdge <= maxLongEdgePx)
            return new RenderOptions { Dpi = dpi };

        var scale = maxLongEdgePx / (double)Math.Max(width, height);
        return new RenderOptions
        {
            Dpi             = dpi,
            Width           = Math.Clamp((int)Math.Round(width * scale), 1, maxLongEdgePx),
            Height          = Math.Clamp((int)Math.Round(height * scale), 1, maxLongEdgePx),
            WithAspectRatio = false
        };
    }

    private static List<byte[]> Render(byte[] pdfBytes, int dpi, int maxPages, int maxLongEdgePx)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);

        var jpegs = new List<byte[]>();

#pragma warning disable CA1416 // PDFtoImage is supported on Windows, Linux and macOS — all target platforms
        var sizes = Conversion.GetPageSizes(pdfBytes);

        for (var page = 0; page < sizes.Count && jpegs.Count < maxPages; page++)
        {
            using var bitmap  = Conversion.ToImage(pdfBytes, page: page, options: OptionsFor(sizes[page], dpi, maxLongEdgePx));
            using var image   = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
            jpegs.Add(encoded.ToArray());
        }
#pragma warning restore CA1416

        return jpegs;
    }
}
