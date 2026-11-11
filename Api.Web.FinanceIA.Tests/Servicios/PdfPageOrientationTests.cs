// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Tests.Legajo;
using iText.IO.Font.Constants;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using TextOrientation = UglyToad.PdfPig.Content.TextOrientation;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Pins how a page whose text is drawn rotated is turned upright for the second extraction pass.
/// Every fixture is built here with iText — generic captions and no real balance. Asserts on the
/// orientation decision and on the rendered image's dimensions, never on pixels.
/// </summary>
public sealed class PdfPageOrientationTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private enum Direction { Upright, RunningUp, RunningDown, UpsideDown }

    private static readonly string[] Captions =
    [
        "Caja y bancos", "Inversiones temporarias", "Creditos por ventas", "Otros creditos",
        "Bienes de cambio", "Total del activo corriente", "Deudas comerciales", "Prestamos",
        "Remuneraciones y cargas sociales", "Cargas fiscales", "Total del pasivo corriente",
    ];

    /// <summary>
    /// One A4 portrait page with a column of captions drawn in <paramref name="direction"/>, the way
    /// a landscape statement is drawn inside a portrait page. <paramref name="pageRotate"/> is the
    /// page's /Rotate.
    /// </summary>
    private static byte[] Page(Direction direction, int pageRotate = 0, int lines = 11)
    {
        using var buffer = new MemoryStream();
        using (var pdf = new PdfDocument(new PdfWriter(buffer)))
        {
            var page = pdf.AddNewPage(PageSize.A4);
            if (pageRotate != 0)
                page.SetRotation(pageRotate);

            var canvas = new PdfCanvas(page);
            var font   = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);

            for (var i = 0; i < lines; i++)
            {
                var caption = Captions[i % Captions.Length];
                canvas.BeginText().SetFontAndSize(font, 11);

                // a b c d e f: the text matrix. Its first column is the baseline direction.
                switch (direction)
                {
                    case Direction.Upright:     canvas.SetTextMatrix(1, 0, 0, 1, 60, 780 - i * 20);  break;
                    case Direction.RunningUp:   canvas.SetTextMatrix(0, 1, -1, 0, 80 + i * 20, 80);  break;
                    case Direction.RunningDown: canvas.SetTextMatrix(0, -1, 1, 0, 500 - i * 20, 760); break;
                    case Direction.UpsideDown:  canvas.SetTextMatrix(-1, 0, 0, -1, 540, 80 + i * 20); break;
                }

                canvas.ShowText(caption).EndText();
            }
        }

        return buffer.ToArray();
    }

    // ── Decision ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_page_with_a_text_layer_is_decided_and_a_scan_is_not()
    {
        var upright = Assert.Single(PdfPageOrientation.DetectPageOrientations(Page(Direction.Upright), maxPages: 10));
        var turned  = Assert.Single(PdfPageOrientation.DetectPageOrientations(Page(Direction.RunningUp), maxPages: 10));
        var scan    = Assert.Single(PdfPageOrientation.DetectPageOrientations(LegajoPdfFactory.BlankPdf(1), maxPages: 10));

        Assert.Equal(new PageOrientation(0, true), upright);
        Assert.Equal(new PageOrientation(90, true), turned);
        Assert.Equal(PageOrientation.Undecided, scan);
    }

    [Fact]
    public void A_page_with_too_few_glyphs_is_undecided_not_upright()
    {
        // One short caption: under MinOrientedLetters, so nothing to go on.
        var page = Assert.Single(PdfPageOrientation.DetectPageOrientations(Page(Direction.RunningUp, lines: 1), maxPages: 10));

        Assert.False(page.Decided);
        Assert.Equal(0, page.Rotation);
    }

    [Fact]
    public void An_unreadable_document_yields_no_decided_page()
    {
        string? failure = null;

        var pages = PdfPageOrientation.DetectPageOrientations("%PDF-1.4 fixture"u8.ToArray(), maxPages: 10, f => failure = f);

        Assert.All(pages, p => Assert.False(p.Decided));
        Assert.NotNull(failure);
    }

    [Fact]
    public void Upright_page_is_not_turned()
    {
        Assert.Equal([0], PdfPageOrientation.DetectUprightRotations(Page(Direction.Upright), maxPages: 10));
    }

    [Fact]
    public void Text_running_up_an_unrotated_page_is_turned_90_clockwise()
    {
        // The production case: a statement drawn rotated inside a portrait page whose /Rotate is 0.
        Assert.Equal([90], PdfPageOrientation.DetectUprightRotations(Page(Direction.RunningUp), maxPages: 10));
    }

    [Fact]
    public void Text_running_down_an_unrotated_page_is_turned_270_clockwise()
    {
        Assert.Equal([270], PdfPageOrientation.DetectUprightRotations(Page(Direction.RunningDown), maxPages: 10));
    }

    [Fact]
    public void Upside_down_text_is_turned_180()
    {
        Assert.Equal([180], PdfPageOrientation.DetectUprightRotations(Page(Direction.UpsideDown), maxPages: 10));
    }

    [Fact]
    public void Horizontal_text_on_a_rotate_90_page_is_turned_back()
    {
        // /Rotate 90 makes a viewer (and PDFium) show horizontal text running DOWN the page.
        Assert.Equal([270], PdfPageOrientation.DetectUprightRotations(Page(Direction.Upright, pageRotate: 90), maxPages: 10));
    }

    [Fact]
    public void Text_drawn_rotated_on_a_page_whose_rotate_compensates_is_already_upright()
    {
        // Running-up text on a /Rotate 90 page is how many landscape PDFs are written: it displays
        // upright, so turning it would be the bug.
        Assert.Equal([0], PdfPageOrientation.DetectUprightRotations(Page(Direction.RunningUp, pageRotate: 90), maxPages: 10));
    }

    [Fact]
    public void Too_few_glyphs_to_trust_are_not_turned()
    {
        // One short caption is below MinOrientedLetters.
        Assert.Equal([0], PdfPageOrientation.DetectUprightRotations(Page(Direction.RunningUp, lines: 1), maxPages: 10));
    }

    [Fact]
    public void Pages_without_text_are_not_turned_and_max_pages_is_honoured()
    {
        Assert.Equal([0, 0], PdfPageOrientation.DetectUprightRotations(LegajoPdfFactory.BlankPdf(3), maxPages: 2));
    }

    [Fact]
    public void Unreadable_bytes_never_throw()
    {
        var failures = new List<string>();

        var rotations = PdfPageOrientation.DetectUprightRotations("%PDF-1.4 fixture"u8.ToArray(), 10, failures.Add);

        Assert.Empty(rotations);
        Assert.Single(failures);
    }

    [Theory]
    [InlineData(30, 0, 0, 0, 0)]  // all horizontal
    [InlineData(2, 0, 0, 28, 90)] // running up
    [InlineData(2, 28, 0, 0, 270)] // running down
    [InlineData(1, 0, 25, 0, 180)] // upside down
    [InlineData(14, 0, 0, 16, 0)] // no 60 % majority
    [InlineData(0, 0, 0, 19, 0)]  // below MinOrientedLetters
    public void Dominant_direction_rule(int horizontal, int rotate90, int rotate180, int rotate270, int expected)
    {
        var orientations = Enumerable.Repeat(TextOrientation.Horizontal, horizontal)
            .Concat(Enumerable.Repeat(TextOrientation.Rotate90, rotate90))
            .Concat(Enumerable.Repeat(TextOrientation.Rotate180, rotate180))
            .Concat(Enumerable.Repeat(TextOrientation.Rotate270, rotate270))
            .Concat(Enumerable.Repeat(TextOrientation.Other, 50)); // never counted

        Assert.Equal(expected, PdfPageOrientation.UprightRotation(orientations));
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_turned_page_renders_landscape_and_an_upright_one_portrait()
    {
        var rotated = Page(Direction.RunningUp);
        var turned  = ClaudeService.RenderPdfPages(rotated, dpi: 72, maxPages: 10,
            PdfPageOrientation.DetectUprightRotations(rotated, 10));
        var asIs    = ClaudeService.RenderPdfPages(rotated, dpi: 72, maxPages: 10);

        var page = Assert.Single(turned.Pages);
        Assert.Equal(90, page.Rotation);
        Assert.True(page.Width > page.Height, $"{page.Width}x{page.Height}");

        var untouched = Assert.Single(asIs.Pages);
        Assert.Equal(0, untouched.Rotation);
        Assert.True(untouched.Height > untouched.Width, $"{untouched.Width}x{untouched.Height}");
        Assert.Equal((untouched.Width, untouched.Height), (page.Height, page.Width));
    }

    [Fact]
    public void Rendering_without_rotations_matches_rendering_with_all_zero_rotations()
    {
        // No rotation means the encode path is untouched — the large-file first pass stays byte-identical.
        var pdf = Page(Direction.Upright);

        var plain = ClaudeService.RenderPdfPages(pdf, dpi: 72, maxPages: 10);
        var zeros = ClaudeService.RenderPdfPages(pdf, dpi: 72, maxPages: 10, [0]);

        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(plain.ImageBlocks),
            System.Text.Json.JsonSerializer.Serialize(zeros.ImageBlocks));
    }
}
