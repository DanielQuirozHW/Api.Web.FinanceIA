// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-09

using Api.Web.FinanceIA.Servicios;
using iText.IO.Font.Constants;
using iText.IO.Image;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using SkiaSharp;

namespace Api.Web.FinanceIA.Tests.BalanceCorrections;

/// <summary>
/// SYNTHETIC one-page PDFs for the text match of a suggested figure, built here with iText (Helvetica 11 at
/// known points) — generic captions and invented figures, never a client file — plus the helpers that render
/// them exactly as the suggestion's renderer does and read the ink back.
/// </summary>
internal static class BalanceSuggestionTextPdf
{
    /// <summary>Font size of every run.</summary>
    public const float FontSize = 11f;

    /// <summary>Upright: drawn left to right. RunningUp: drawn up the page (a landscape table in a portrait page).</summary>
    public enum Direction { Upright, RunningUp }

    /// <summary>One text run: drawn from (X, Y), in PDF user space (points, bottom-left origin).</summary>
    public sealed record Run(string Text, float X, float Y);

    /// <summary>A filler block of captions, so every page has enough glyphs for its orientation to be decided.</summary>
    public static IEnumerable<Run> Filler(float x, float y, Direction direction = Direction.Upright)
    {
        string[] captions = ["Caja y bancos", "Creditos por ventas", "Bienes de cambio"];
        for (var i = 0; i < captions.Length; i++)
        {
            yield return direction == Direction.Upright
                ? new Run(captions[i], x, y - i * 20)
                : new Run(captions[i], x + i * 20, y);
        }
    }

    /// <summary>One page of <paramref name="size"/> (A4 portrait by default) carrying <paramref name="runs"/>.</summary>
    public static byte[] Page(
        IEnumerable<Run> runs, Direction direction = Direction.Upright, int pageRotate = 0, PageSize? size = null) =>
        Document(new PageSpec(runs, size, direction, pageRotate));

    /// <summary>One page of a <see cref="Document"/>.</summary>
    public sealed record PageSpec(
        IEnumerable<Run> Runs, PageSize? Size = null, Direction Direction = Direction.Upright, int PageRotate = 0);

    /// <summary>A document of one page per <paramref name="pages"/> entry, in order.</summary>
    public static byte[] Document(params PageSpec[] pages)
    {
        using var buffer = new MemoryStream();
        using (var pdf = new PdfDocument(new PdfWriter(buffer)))
        {
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            foreach (var spec in pages)
            {
                var page = pdf.AddNewPage(spec.Size ?? PageSize.A4);
                if (spec.PageRotate != 0)
                    page.SetRotation(spec.PageRotate);

                var canvas = new PdfCanvas(page);
                foreach (var run in spec.Runs)
                {
                    canvas.BeginText().SetFontAndSize(font, FontSize);
                    if (spec.Direction == Direction.Upright)
                        canvas.SetTextMatrix(1, 0, 0, 1, run.X, run.Y);
                    else
                        canvas.SetTextMatrix(0, 1, -1, 0, run.X, run.Y);

                    canvas.ShowText(run.Text).EndText();
                }
            }
        }

        return buffer.ToArray();
    }

    /// <summary>The advance width of <paramref name="text"/> in Helvetica <see cref="FontSize"/>, in points.</summary>
    public static float Width(string text) =>
        PdfFontFactory.CreateFont(StandardFonts.HELVETICA).GetWidth(text, FontSize);

    /// <summary>A "scan": one A4 page holding only an image of dark bars — no text layer at all.</summary>
    public static byte[] Scan()
    {
        byte[] png;
        using (var bitmap = new SKBitmap(600, 850))
        {
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.White);
                using var paint = new SKPaint { Color = SKColors.Black };
                for (var row = 0; row < 10; row++)
                    canvas.DrawRect(new SKRect(60, 80 + row * 60, 540, 100 + row * 60), paint);
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data  = image.Encode(SKEncodedImageFormat.Png, 100);
            png = data.ToArray();
        }

        using var buffer = new MemoryStream();
        using (var pdf = new PdfDocument(new PdfWriter(buffer)))
        {
            var page   = pdf.AddNewPage(PageSize.A4);
            var canvas = new PdfCanvas(page);
            canvas.AddImageFittedIntoRectangle(ImageDataFactory.Create(png), new Rectangle(0, 0, 595, 842), false);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// The page's full image exactly as the suggestion's renderer produces it: turned upright by the glyph
    /// decision (what the renderer's plan applies to every text page), at 100 DPI.
    /// </summary>
    public static RenderedImage Render(byte[] pdf) => RenderAll(pdf).Single();

    /// <summary>Every page's full image as <see cref="Render"/> produces it, numbered 1.. in page order.</summary>
    public static IReadOnlyList<RenderedImage> RenderAll(byte[] pdf)
    {
        var rotations = PdfPageOrientation.DetectUprightRotations(pdf, maxPages: 10);
        var rendered  = ClaudeService.RenderPdfPages(pdf, 100, 10, rotations);
        return rendered.Images!.Where(i => i.Half == RenderedImage.FullHalf).ToList();
    }

    /// <summary>Share of dark pixels (luma &lt; 128) of <paramref name="image"/> inside the fractional rectangle.</summary>
    public static double Ink(RenderedImage image, double top, double bottom, double left, double right)
    {
        using var bitmap = SKBitmap.Decode(Convert.FromBase64String(image.Base64Jpeg));
        var x0 = (int)Math.Floor(Math.Clamp(left, 0, 1) * bitmap.Width);
        var x1 = (int)Math.Ceiling(Math.Clamp(right, 0, 1) * bitmap.Width);
        var y0 = (int)Math.Floor(Math.Clamp(top, 0, 1) * bitmap.Height);
        var y1 = (int)Math.Ceiling(Math.Clamp(bottom, 0, 1) * bitmap.Height);

        int dark = 0, total = 0;
        for (var y = y0; y < Math.Min(y1, bitmap.Height); y++)
        {
            for (var x = x0; x < Math.Min(x1, bitmap.Width); x++)
            {
                var c = bitmap.GetPixel(x, y);
                total++;
                if (0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue < 128)
                    dark++;
            }
        }

        return total == 0 ? 0 : (double)dark / total;
    }
}
