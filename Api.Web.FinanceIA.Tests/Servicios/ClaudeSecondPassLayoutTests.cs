// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using System.Text.Json;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Tests.Legajo;
using iText.Kernel.Pdf;
using SkiaSharp;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The layout of the extraction's second pass, without a model: which pages are split into halves under
/// the image budget, what the renderer emits for them, the byte budget and the many-image size cap, and
/// the closing text that names the images. Every fixture is a synthetic empty page.
/// </summary>
public sealed class ClaudeSecondPassLayoutTests
{
    private static readonly (float Width, float Height) Portrait  = (595, 842);
    private static readonly (float Width, float Height) Landscape = (842, 595);

    // ── PlanSplits ────────────────────────────────────────────────────────────

    [Fact]
    public void A_landscape_page_is_split_and_a_portrait_one_is_not()
    {
        var splits = ClaudeService.PlanSplits([Portrait, Landscape], [0, 0], imageBudget: 100);

        Assert.Equal([1], splits);
    }

    [Fact]
    public void A_portrait_page_turned_a_quarter_is_split_and_a_landscape_page_turned_a_quarter_is_not()
    {
        var splits = ClaudeService.PlanSplits([Portrait, Landscape, Portrait], [90, 270, 180], imageBudget: 100);

        Assert.Equal([0], splits);
    }

    [Fact]
    public void A_page_just_under_the_ratio_is_not_split()
    {
        Assert.Empty(ClaudeService.PlanSplits([(124, 100)], [0], imageBudget: 100));
        Assert.Equal([0], ClaudeService.PlanSplits([(125, 100)], [0], imageBudget: 100));
    }

    [Fact]
    public void With_no_room_for_two_more_images_nothing_is_split()
    {
        // 49 pages at the 150 DPI tier's budget of 50: one image short of a pair of halves.
        var sizes = Enumerable.Repeat(Landscape, 49).ToList();

        Assert.Empty(ClaudeService.PlanSplits(sizes, new int[49], imageBudget: 50));
    }

    [Fact]
    public void Turned_pages_are_split_first_then_the_rest_in_page_order()
    {
        // 45 pages and a budget of 50: room for two pairs of halves.
        var sizes     = Enumerable.Repeat(Landscape, 45).ToList();
        var rotations = new int[45];
        sizes[30] = Portrait; rotations[30] = 90; // turned upright, landscape then

        var splits = ClaudeService.PlanSplits(sizes, rotations, imageBudget: 50);

        Assert.Equal([0, 30], splits.Order());
    }

    [Fact]
    public void The_budget_never_exceeds_the_api_image_cap()
    {
        var sizes  = Enumerable.Repeat(Landscape, 40).ToList();
        var splits = ClaudeService.PlanSplits(sizes, new int[40], imageBudget: ClaudeService.MaxRequestImages);

        Assert.Equal(30, splits.Count); // 40 pages + 2 × 30 halves = 100
    }

    // ── RenderPdfPages ────────────────────────────────────────────────────────

    [Fact]
    public void The_first_pass_render_is_unchanged_when_no_layout_is_given()
    {
        var pdf = LegajoPdfFactory.SizedPdf(842, 595);

        var plain  = ClaudeService.RenderPdfPages(pdf, 96, 30);
        var turned = ClaudeService.RenderPdfPages(pdf, 96, 30, [0]);

        Assert.Single(plain.ImageBlocks);
        Assert.False(plain.Pages[0].Split);
        Assert.Equal(JsonSerializer.Serialize(plain.ImageBlocks), JsonSerializer.Serialize(turned.ImageBlocks));
    }

    [Fact]
    public void A_page_in_the_layout_goes_as_full_left_and_right()
    {
        var layout = new ClaudeService.SecondPassLayout(new HashSet<int> { 0 }, ClaudeService.SplitImageBytesBudget, null);

        var rendered = ClaudeService.RenderPdfPages(LegajoPdfFactory.SizedPdf(842, 595), 96, 30, [0], layout);

        Assert.Equal(3, rendered.ImageBlocks.Count);
        Assert.Equal(1, rendered.SplitPages);
        Assert.True(rendered.Base64Bytes > 0);
    }

    [Fact]
    public void A_page_in_the_layout_that_is_not_wide_once_rendered_is_not_split()
    {
        var layout = new ClaudeService.SecondPassLayout(new HashSet<int> { 0 }, ClaudeService.SplitImageBytesBudget, null);

        var rendered = ClaudeService.RenderPdfPages(LegajoPdfFactory.SizedPdf(595, 842), 96, 30, [0], layout);

        Assert.Single(rendered.ImageBlocks);
        Assert.Equal(0, rendered.SplitPages);
    }

    [Fact]
    public void Halves_that_would_exceed_the_byte_budget_are_dropped_but_the_full_page_is_kept()
    {
        var layout = new ClaudeService.SecondPassLayout(new HashSet<int> { 0 }, MaxBase64Bytes: 1, null);

        var rendered = ClaudeService.RenderPdfPages(LegajoPdfFactory.SizedPdf(842, 595), 96, 30, [0], layout);

        Assert.Single(rendered.ImageBlocks);
        Assert.Equal(0, rendered.SplitPages);
    }

    [Fact]
    public void The_many_image_cap_holds_every_image_within_its_side()
    {
        var layout = new ClaudeService.SecondPassLayout(new HashSet<int> { 0 }, ClaudeService.SplitImageBytesBudget, MaxSidePx: 300);

        var rendered = ClaudeService.RenderPdfPages(LegajoPdfFactory.SizedPdf(842, 595), 150, 30, [0], layout);

        Assert.Equal(3, rendered.ImageBlocks.Count);
        foreach (var block in rendered.ImageBlocks)
        {
            using var image = Decode(block);
            Assert.True(Math.Max(image.Width, image.Height) <= 300, $"{image.Width}x{image.Height}");
        }
    }

    [Fact]
    public void The_halves_overlap_past_the_midline()
    {
        var (left, right) = ClaudeService.HalfRects(1000, 700);

        Assert.Equal(new SKRectI(0, 0, 560, 700), left);
        Assert.Equal(new SKRectI(440, 0, 1000, 700), right);
    }

    // ── The closing text ──────────────────────────────────────────────────────

    [Fact]
    public void The_closing_text_names_each_image_of_a_split_page()
    {
        ClaudeService.RenderedPage[] pages =
        [
            new(100, 140, 0),
            new(140, 100, 90, Split: true),
            new(100, 140, 0),
            new(140, 100, 0, Split: true),
        ];
        var rendered = new ClaudeService.RenderedPdf([], 4, 4, pages);

        var text = ClaudeService.SecondPassText("balance", 2025, rendered);

        Assert.Contains("page 2 is image 2 (full page), image 3 (its left half) and image 4 (its right half); " +
                        "page 4 is image 6 (full page), image 7 (its left half) and image 8 (its right half).", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_split_the_closing_text_has_no_image_list_but_keeps_the_reading_rules()
    {
        var rendered = new ClaudeService.RenderedPdf([], 1, 1, [new(100, 140, 0)]);

        var text = ClaudeService.SecondPassText("balance", null, rendered);

        Assert.Equal(
            "Analyse this balance financial document and return the JSON result. " +
            ClaudeService.TwoSidedSheetRule + " " + ClaudeService.DashIsZeroRule,
            text);
    }

    // ── Page sizes follow the page as displayed ───────────────────────────────

    [Fact]
    public void A_rotate_90_page_is_measured_and_rendered_as_displayed()
    {
        // The split plan reads page sizes before rendering: they must match what the renderer draws.
        var pdf = Rotated(LegajoPdfFactory.SizedPdf(595, 842), 90);

#pragma warning disable CA1416
        var size = PDFtoImage.Conversion.GetPageSizes(pdf)[0];
#pragma warning restore CA1416
        var rendered = ClaudeService.RenderPdfPages(pdf, 72, 1);

        Assert.Equal(size.Width > size.Height, rendered.Pages[0].Width > rendered.Pages[0].Height);
        Assert.True(rendered.Pages[0].Width > rendered.Pages[0].Height);
    }

    private static byte[] Rotated(byte[] pdfBytes, int degrees)
    {
        using var buffer = new MemoryStream();
        using (var pdf = new PdfDocument(new PdfReader(new MemoryStream(pdfBytes)), new PdfWriter(buffer)))
        {
            pdf.GetPage(1).SetRotation(degrees);
        }

        return buffer.ToArray();
    }

    private static SKBitmap Decode(object block)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(block));
        return SKBitmap.Decode(Convert.FromBase64String(doc.RootElement.GetProperty("source").GetProperty("data").GetString()!));
    }
}
