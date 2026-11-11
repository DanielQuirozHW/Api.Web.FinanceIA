// Project : Api.Web.FinanceIA
// Created : 2026-11-03

using Api.Web.FinanceIA.Models.Financia;
using SkiaSharp;

namespace Api.Web.FinanceIA.Servicios.BalanceCorrections;

/// <summary>
/// The evidence of a suggested figure, built to survive an imprecise row band (on scans the model's band
/// can miss the row by several lines):
/// <list type="bullet">
///   <item>a crop of the image the model read it from (the upright page, or the half) around the row it
///   reported — generous on purpose, ±<see cref="PaddingShare"/> of the height around the band at the
///   image's full width, held to <see cref="MaxBytes"/>;</item>
///   <item>the upright FULL page itself (<see cref="Pages"/>), downscaled, with a
///   <see cref="Highlight"/> rectangle in fractions of that page, so the analyst can look around the band.</item>
/// </list>
/// Never logged, never persisted.
/// </summary>
public static class BalanceSuggestionEvidence
{
    /// <summary>Padding added above and below the reported band, as a share of the image height.</summary>
    public const double PaddingShare = 0.07;

    /// <summary>Largest crop JPEG returned, in bytes.</summary>
    public const int MaxBytes = 200 * 1024;

    /// <summary>Longest side of a returned full page, in pixels (never upscaled).</summary>
    public const int PageMaxSidePx = 1600;

    /// <summary>JPEG quality a full page is first encoded at.</summary>
    public const int PageJpegQuality = 70;

    /// <summary>Largest full-page JPEG returned, in bytes.</summary>
    public const int PageMaxBytes = 450 * 1024;

    /// <summary>The media type of every crop and page.</summary>
    public const string MediaType = "image/jpeg";

    private const int CropQuality = 80;
    private const int MinQuality  = 40;
    private const int QualityStep = 15;
    private const int MinWidthPx  = 200;

    /// <summary>True when [top, bottom] is a usable band: both finite, 0 ≤ top &lt; bottom ≤ 1.</summary>
    public static bool IsValidBand(double? top, double? bottom) =>
        top is { } t && bottom is { } b
        && double.IsFinite(t) && double.IsFinite(b)
        && t >= 0d && b <= 1d && t < b;

    /// <summary>
    /// The crop of <paramref name="image"/> for the band [<paramref name="top"/>, <paramref name="bottom"/>]
    /// (fractions of its height), or null when the band is missing or invalid, or the image cannot be
    /// decoded or squeezed under <see cref="MaxBytes"/>.
    /// </summary>
    public static BalanceSuggestionEvidenceResponse? Crop(RenderedImage image, double? top, double? bottom)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (CropRect(image.Width, image.Height, top, bottom) is null)
            return null;

        using var bitmap = Decode(image);
        if (bitmap is null)
            return null;

        // The rectangle is recomputed on the decoded size: the recorded one is the same in practice, but
        // the decoded bitmap is what is cut.
        if (CropRect(bitmap.Width, bitmap.Height, top, bottom) is not { } rect)
            return null;

        using var source = SKImage.FromBitmap(bitmap);
        using var cut    = source.Subset(rect);
        if (cut is null)
            return null;

        return Encode(cut, CropQuality, MaxBytes) is { } bytes
            ? new BalanceSuggestionEvidenceResponse(MediaType, Convert.ToBase64String(bytes))
            : null;
    }

    /// <summary>
    /// The pixel rectangle of the band on a <paramref name="width"/> × <paramref name="height"/> image:
    /// full width, the band widened by <see cref="PaddingShare"/> of the height on each side and clamped to
    /// the image. Null when the band is not <see cref="IsValidBand">valid</see>.
    /// </summary>
    public static SKRectI? CropRect(int width, int height, double? top, double? bottom)
    {
        if (width <= 0 || height <= 0 || !IsValidBand(top, bottom))
            return null;

        var y0 = (int)Math.Floor(Math.Max(0d, top!.Value - PaddingShare) * height);
        var y1 = (int)Math.Ceiling(Math.Min(1d, bottom!.Value + PaddingShare) * height);
        y1 = Math.Min(height, Math.Max(y1, y0 + 1));

        return new SKRectI(0, y0, width, y1);
    }

    /// <summary>
    /// The band read on <paramref name="image"/> as a rectangle on its FULL page: the vertical fractions are
    /// the band as reported (a half has the page's height); the horizontal ones are the image's x-range on
    /// the page — 0..1 for the full page, the half's own range (<see cref="ClaudeService.HalfRects"/> on the
    /// page's full image) for a half. Null when the band is invalid, or a half's full page is not among
    /// <paramref name="images"/>.
    /// </summary>
    public static BalanceSuggestionHighlight? Highlight(
        RenderedImage image, IReadOnlyList<RenderedImage> images, double? top, double? bottom)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(images);

        if (!IsValidBand(top, bottom))
            return null;

        double left = 0d, right = 1d;
        if (!string.Equals(image.Half, RenderedImage.FullHalf, StringComparison.Ordinal))
        {
            var full = FullPage(images, image.Page);
            if (full is null || full.Width <= 0 || full.Height <= 0)
                return null;

            var (leftRect, rightRect) = ClaudeService.HalfRects(full.Width, full.Height);
            var rect = string.Equals(image.Half, RenderedImage.LeftHalf, StringComparison.Ordinal) ? leftRect
                     : string.Equals(image.Half, RenderedImage.RightHalf, StringComparison.Ordinal) ? rightRect
                     : (SKRectI?)null;
            if (rect is not { } r)
                return null;

            left  = (double)r.Left  / full.Width;
            right = (double)r.Right / full.Width;
        }

        return new BalanceSuggestionHighlight(image.Page, top!.Value, bottom!.Value, left, right);
    }

    /// <summary>
    /// One entry per distinct page the <paramref name="items"/> refer to (each one's <c>Page</c>, and a
    /// derived one's <c>SecondaryPage</c>), ascending: that page's upright
    /// full image as sent to the model, downscaled to ≤ <see cref="PageMaxSidePx"/> on the long side and
    /// encoded as JPEG (quality <see cref="PageJpegQuality"/>, then lower, then smaller) to ≤
    /// <see cref="PageMaxBytes"/>. A page whose image is missing or cannot be encoded is left out.
    /// </summary>
    public static IReadOnlyList<BalanceSuggestionPageResponse> Pages(
        IReadOnlyList<BalanceCorrectionSuggestionItem> items, IReadOnlyList<RenderedImage> images)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(images);

        var pages = new List<BalanceSuggestionPageResponse>();
        var referenced = items.Select(i => i.Page).Concat(items.Select(i => i.SecondaryPage))
            .Where(p => p is not null).Select(p => p!.Value).Distinct().Order();
        foreach (var page in referenced)
        {
            if (FullPage(images, page) is not { } full)
                continue;

            if (PageImage(full) is { } image)
                pages.Add(new BalanceSuggestionPageResponse(page, image));
        }

        return pages;
    }

    /// <summary>The page JPEG of <see cref="Pages"/> for one full image, or null when it cannot be produced.</summary>
    public static BalanceSuggestionEvidenceResponse? PageImage(RenderedImage full)
    {
        ArgumentNullException.ThrowIfNull(full);

        using var bitmap = Decode(full);
        if (bitmap is null)
            return null;

        var longSide = Math.Max(bitmap.Width, bitmap.Height);
        SKBitmap? resized = null;
        try
        {
            if (longSide > PageMaxSidePx)
            {
                var factor = (double)PageMaxSidePx / longSide;
                var width  = Math.Max(1, (int)Math.Round(bitmap.Width * factor));
                var height = Math.Max(1, (int)Math.Round(bitmap.Height * factor));
                resized = bitmap.Resize(new SKImageInfo(width, height), SKFilterQuality.Medium);
                if (resized is null)
                    return null;
            }

            using var image = SKImage.FromBitmap(resized ?? bitmap);
            return Encode(image, PageJpegQuality, PageMaxBytes) is { } bytes
                ? new BalanceSuggestionEvidenceResponse(MediaType, Convert.ToBase64String(bytes))
                : null;
        }
        finally
        {
            resized?.Dispose();
        }
    }

    /// <summary>The full (not half) image of <paramref name="page"/> among <paramref name="images"/>, or null.</summary>
    internal static RenderedImage? FullPage(IReadOnlyList<RenderedImage> images, int page) =>
        images.FirstOrDefault(i => i.Page == page && string.Equals(i.Half, RenderedImage.FullHalf, StringComparison.Ordinal));

    private static SKBitmap? Decode(RenderedImage image)
    {
        byte[] jpeg;
        try
        {
            jpeg = Convert.FromBase64String(image.Base64Jpeg);
        }
        catch (FormatException)
        {
            return null;
        }

        return SKBitmap.Decode(jpeg);
    }

    // JPEG at falling quality, then at falling width, until it fits; null when even the smallest does not.
    private static byte[]? Encode(SKImage image, int startQuality, int maxBytes)
    {
        SKImage? scaled = null;
        try
        {
            var current = image;
            while (true)
            {
                for (var quality = startQuality; quality >= MinQuality; quality -= QualityStep)
                {
                    using var data = current.Encode(SKEncodedImageFormat.Jpeg, quality);
                    if (data is not null && data.Size <= maxBytes)
                        return data.ToArray();
                }

                var width = (int)(current.Width * 0.75);
                if (width < MinWidthPx)
                    return null;

                var height = Math.Max(1, (int)(current.Height * 0.75));
                using var bitmap = SKBitmap.FromImage(current);
                using var resized = bitmap.Resize(new SKImageInfo(width, height), SKFilterQuality.Medium);
                if (resized is null)
                    return null;

                var next = SKImage.FromBitmap(resized);
                scaled?.Dispose();
                scaled  = next;
                current = next;
            }
        }
        finally
        {
            scaled?.Dispose();
        }
    }
}
