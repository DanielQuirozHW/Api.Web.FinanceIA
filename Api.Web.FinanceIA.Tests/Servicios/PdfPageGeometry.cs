// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-09

using iText.IO.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The text runs and painted paths of one page, in PDF user space (y up), for geometry assertions on the page
/// furniture. A path's <see cref="PathGeo.Ink"/> is its geometry widened by half the line width when it is stroked,
/// so it matches what the eye sees (and what <c>IPdfBrand.SymbolInk</c> describes), not the bare outline.
/// </summary>
internal static class PdfPageGeometry
{
    internal sealed record Run(string Text, float Left, float Right, float Baseline, float FontSize, string Font = "");

    internal sealed record PathGeo(Rectangle Ink, bool Fills, bool Strokes, bool IsClosed, PdfThemeInspector.Rgb? Fill, PdfThemeInspector.Rgb? Stroke);

    internal sealed record Page(Rectangle Size, IReadOnlyList<Run> Runs, IReadOnlyList<PathGeo> Paths);

    /// <summary>Reads page <paramref name="pageNumber"/> of <paramref name="pdfBytes"/>.</summary>
    internal static Page Read(byte[] pdfBytes, int pageNumber)
    {
        using var pdf = new PdfDocument(new PdfReader(new MemoryStream(pdfBytes)));
        var listener = new Listener();
        PdfPage page = pdf.GetPage(pageNumber);
        new PdfCanvasProcessor(listener).ProcessPageContent(page);
        return new Page(page.GetPageSize(), listener.Runs, listener.Paths);
    }

    /// <summary>The number of pages of <paramref name="pdfBytes"/>.</summary>
    internal static int PageCount(byte[] pdfBytes)
    {
        using var pdf = new PdfDocument(new PdfReader(new MemoryStream(pdfBytes)));
        return pdf.GetNumberOfPages();
    }

    /// <summary>A brand face as shipped (Assets/Fonts in the test output), for its cap height and glyph bearings.</summary>
    internal static FontProgram BrandFont(string file) =>
        FontProgramFactory.CreateFont(File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", file)), false);

    /// <summary>The face's cap height at <paramref name="size"/> points.</summary>
    internal static float CapHeight(FontProgram font, float size) => font.GetFontMetrics().GetCapHeight() * size / 1000f;

    /// <summary>The left side bearing of <paramref name="c"/> at <paramref name="size"/> points: where its ink starts.</summary>
    internal static float LeftBearing(FontProgram font, char c, float size) =>
        (font.GetGlyph(c)?.GetBbox() is { } bbox ? bbox[0] : 0) * size / 1000f;

    /// <summary>The union of the paths' ink.</summary>
    internal static Rectangle Union(IEnumerable<PathGeo> paths)
    {
        List<Rectangle> inks = paths.Select(p => p.Ink).ToList();
        float left   = inks.Min(r => r.GetLeft());
        float bottom = inks.Min(r => r.GetBottom());
        float right  = inks.Max(r => r.GetRight());
        float top    = inks.Max(r => r.GetTop());
        return new Rectangle(left, bottom, right - left, top - bottom);
    }

    private sealed class Listener : IEventListener
    {
        public List<Run>     Runs  { get; } = [];
        public List<PathGeo> Paths { get; } = [];

        public void EventOccurred(IEventData data, EventType type)
        {
            switch (data)
            {
                case TextRenderInfo text when type == EventType.RENDER_TEXT:
                    if (string.IsNullOrWhiteSpace(text.GetText())) return;
                    LineSegment baseline = text.GetBaseline();
                    Runs.Add(new Run(
                        text.GetText(),
                        baseline.GetStartPoint().Get(Vector.I1),
                        baseline.GetEndPoint().Get(Vector.I1),
                        baseline.GetStartPoint().Get(Vector.I2),
                        text.GetFontSize(),
                        text.GetFont()?.GetFontProgram()?.GetFontNames()?.GetFontName() ?? string.Empty));
                    break;

                case PathRenderInfo path when type == EventType.RENDER_PATH:
                    OnPath(path);
                    break;
            }
        }

        public ICollection<EventType>? GetSupportedEvents() => null;

        private void OnPath(PathRenderInfo info)
        {
            bool fills   = (info.GetOperation() & PathRenderInfo.FILL) != 0;
            bool strokes = (info.GetOperation() & PathRenderInfo.STROKE) != 0;
            if (!fills && !strokes) return;

            Matrix ctm = info.GetCtm();
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            bool any = false, closed = true;

            foreach (Subpath subpath in info.GetPath().GetSubpaths())
            {
                if (subpath.GetSegments().Count == 0) continue;
                closed &= subpath.IsClosed();

                foreach (IShape segment in subpath.GetSegments())
                {
                    foreach (Point p in segment.GetBasePoints())
                    {
                        Vector v = new Vector((float)p.GetX(), (float)p.GetY(), 1).Cross(ctm);
                        minX = Math.Min(minX, v.Get(Vector.I1)); maxX = Math.Max(maxX, v.Get(Vector.I1));
                        minY = Math.Min(minY, v.Get(Vector.I2)); maxY = Math.Max(maxY, v.Get(Vector.I2));
                        any  = true;
                    }
                }
            }

            if (!any) return;

            // Half the line width (scaled by the CTM, which is uniform here) on every side of a stroked outline.
            float half = strokes ? info.GetLineWidth() * ctm.Get(Matrix.I11) / 2f : 0f;
            var ink = new Rectangle(minX - half, minY - half, (maxX - minX) + (2f * half), (maxY - minY) + (2f * half));

            Paths.Add(new PathGeo(
                ink, fills, strokes, closed,
                fills ? PdfThemeInspector.Rgb.From(info.GetFillColor()) : null,
                strokes ? PdfThemeInspector.Rgb.From(info.GetStrokeColor()) : null));
        }
    }
}
