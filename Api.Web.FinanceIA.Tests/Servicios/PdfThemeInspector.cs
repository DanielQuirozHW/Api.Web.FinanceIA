// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using IPath = iText.Kernel.Geom.Path;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Walks every page's content (form XObjects included, with their matrices) and records, in drawing order, each
/// filled shape and each text run with the fill it is painted in. A text run's BACKGROUND is the last shape filled
/// before it whose bounds contain the run's centre — the page paper, a card, a table header, a bar segment — so
/// contrast is measured against what is really behind the glyphs.
/// </summary>
internal static class PdfThemeInspector
{
    internal sealed record Rgb(int R, int G, int B)
    {
        public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";

        internal static Rgb From(DeviceRgb color) => From((Color)color)!;

        internal static Rgb? From(Color? color)
        {
            if (color is null) return null;
            float[] v = color.GetColorValue();
            return v.Length switch
            {
                1 => new Rgb(To255(v[0]), To255(v[0]), To255(v[0])),
                3 => new Rgb(To255(v[0]), To255(v[1]), To255(v[2])),
                _ => null   // CMYK / patterns: none are drawn by the reports
            };
        }

        private static int To255(float value) => (int)Math.Round(value * 255.0);
    }

    internal sealed record Fill(int Page, Rectangle Bounds, Rgb Color);

    internal sealed record TextRun(int Page, string Text, Rgb? Color, Rgb Background, float X, float Y);

    internal sealed record PathPaint(int Page, Rgb? Fill, Rgb? Stroke, bool Fills, bool Strokes, Rectangle Bounds);

    internal sealed record Report(IReadOnlyList<TextRun> Text, IReadOnlyList<PathPaint> Paths, IReadOnlyList<Fill> FirstFills, int Pages, Rectangle PageSize);

    internal static Report Inspect(byte[] pdfBytes, Rgb paper)
    {
        using var reader = new PdfReader(new MemoryStream(pdfBytes));
        using var pdf    = new PdfDocument(reader);

        var text  = new List<TextRun>();
        var paths = new List<PathPaint>();
        var first = new List<Fill>();

        for (int i = 1; i <= pdf.GetNumberOfPages(); i++)
        {
            var listener = new Listener(i, paper);
            new PdfCanvasProcessor(listener).ProcessPageContent(pdf.GetPage(i));
            text.AddRange(listener.Text);
            paths.AddRange(listener.Paths);
            if (listener.Fills.Count > 0) first.Add(listener.Fills[0]);
        }

        return new Report(text, paths, first, pdf.GetNumberOfPages(), pdf.GetPage(1).GetPageSize());
    }

    /// <summary>WCAG 2 contrast ratio between two sRGB colours.</summary>
    internal static double Contrast(Rgb a, Rgb b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    internal static double Contrast(DeviceRgb a, DeviceRgb b) => Contrast(Rgb.From(a), Rgb.From(b));

    private static double Luminance(Rgb c)
    {
        static double Lin(int v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Lin(c.R)) + (0.7152 * Lin(c.G)) + (0.0722 * Lin(c.B));
    }

    private sealed class Listener(int page, Rgb paper) : IEventListener
    {
        public List<Fill>      Fills { get; } = [];
        public List<TextRun>   Text  { get; } = [];
        public List<PathPaint> Paths { get; } = [];

        public void EventOccurred(IEventData data, EventType type)
        {
            switch (data)
            {
                case PathRenderInfo path when type == EventType.RENDER_PATH:
                    OnPath(path);
                    break;
                case TextRenderInfo run when type == EventType.RENDER_TEXT:
                    OnText(run);
                    break;
            }
        }

        public ICollection<EventType>? GetSupportedEvents() => null;

        private void OnPath(PathRenderInfo info)
        {
            bool fills   = (info.GetOperation() & PathRenderInfo.FILL) != 0;
            bool strokes = (info.GetOperation() & PathRenderInfo.STROKE) != 0;
            if (!fills && !strokes) return;   // a clipping path only

            Rectangle? bounds = Bounds(info.GetPath(), info.GetCtm());
            if (bounds is null) return;

            Rgb? fill   = fills   ? Rgb.From(info.GetFillColor())   : null;
            Rgb? stroke = strokes ? Rgb.From(info.GetStrokeColor()) : null;
            Paths.Add(new PathPaint(page, fill, stroke, fills, strokes, bounds));

            if (fills && fill is not null)
                Fills.Add(new Fill(page, bounds, fill));
        }

        private void OnText(TextRenderInfo info)
        {
            string text = info.GetText();
            if (string.IsNullOrWhiteSpace(text) || info.GetTextRenderMode() == 3) return;   // spaces, invisible text

            Vector start  = info.GetBaseline().GetStartPoint();
            Vector end    = info.GetBaseline().GetEndPoint();
            Vector ascent = info.GetAscentLine().GetStartPoint();

            float x = (start.Get(Vector.I1) + end.Get(Vector.I1)) / 2f;
            float y = (start.Get(Vector.I2) + ascent.Get(Vector.I2)) / 2f;

            Rgb background = paper;
            for (int i = Fills.Count - 1; i >= 0; i--)
            {
                Rectangle r = Fills[i].Bounds;
                if (x >= r.GetLeft() && x <= r.GetRight() && y >= r.GetBottom() && y <= r.GetTop())
                {
                    background = Fills[i].Color;
                    break;
                }
            }

            Text.Add(new TextRun(page, text, Rgb.From(info.GetFillColor()), background, x, y));
        }

        private static Rectangle? Bounds(IPath path, Matrix ctm)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            bool any = false;

            foreach (Subpath subpath in path.GetSubpaths())
            {
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

            return any ? new Rectangle(minX, minY, maxX - minX, maxY - minY) : null;
        }
    }
}
