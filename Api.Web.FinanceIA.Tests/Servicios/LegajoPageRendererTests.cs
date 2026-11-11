// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-28

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using SkiaSharp;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The real PDFium renderer's long-edge cap (an A4 page at 200 DPI is 1654×2339 px; the extraction
/// sends at most 1568), and <see cref="LegajoJsonPointer"/>'s addressing.
/// </summary>
public sealed class LegajoPageRendererTests
{
    [Fact]
    public void RenderJpegs_WithALongEdgeCap_DownscalesKeepingTheAspectRatio()
    {
        var pdf = LegajoPdfFactory.NativePdf(LegajoPdfFactory.SampleText, LegajoPdfFactory.SampleText);
        var renderer = new PdfToImagePageRenderer();

        var capped   = renderer.RenderJpegs(pdf, 200, 10, maxLongEdgePx: 1568);
        var uncapped = renderer.RenderJpegs(pdf, 72, 10);

        Assert.Equal(2, capped.Count);
        using var page = SKBitmap.Decode(capped[0]);
        Assert.Equal(1568, Math.Max(page.Width, page.Height));

        using var small = SKBitmap.Decode(uncapped[0]);
        Assert.Equal((double)small.Width / small.Height, (double)page.Width / page.Height, precision: 2);
    }

    [Fact]
    public void RenderJpegs_PageAlreadySmaller_IsNotUpscaled()
    {
        var pdf = LegajoPdfFactory.NativePdf(LegajoPdfFactory.SampleText);

        var jpegs = new PdfToImagePageRenderer().RenderJpegs(pdf, 72, 10, maxLongEdgePx: 5000);

        using var page = SKBitmap.Decode(jpegs[0]);
        Assert.True(Math.Max(page.Width, page.Height) < 1000);
    }

    [Fact]
    public void RenderJpegs_OversizedPage_IsRenderedStraightAtTheCap_ForBothCallers()
    {
        // 10000 × 14000 pt: at 200 DPI that is ~27778 × 38889 px (≈ 4 GB as RGBA) if rendered first
        // and downscaled after. It must come out capped, and the test would not survive the old path.
        var pdf = LegajoPdfFactory.SizedPdf(10_000f, 14_000f);
        var renderer = new PdfToImagePageRenderer();

        using var extraction     = SKBitmap.Decode(Assert.Single(renderer.RenderJpegs(pdf, 200, 10, maxLongEdgePx: 1568)));
        using var classification = SKBitmap.Decode(Assert.Single(renderer.RenderJpegs(pdf, 100, 10)));

        Assert.Equal(1568, extraction.Height);
        Assert.Equal(1120, extraction.Width);
        Assert.Equal(PdfToImagePageRenderer.ClassificationMaxLongEdgePx, classification.Height);
        Assert.Equal(1120, classification.Width);
    }

    [Fact]
    public void RenderJpegs_ClassificationOfANormalPage_IsByteIdenticalToPhase2a()
    {
        // The Phase 2a renderer, verbatim: ToImages at the DPI, JPEG q80. A normal page (A4 here, what
        // iText's Document defaults to) must take the unchanged DPI path, so the classification payload
        // is byte-identical — the golden request tests use a stub renderer and cannot prove this part.
        var pdf = LegajoPdfFactory.NativePdf(LegajoPdfFactory.SampleText, LegajoPdfFactory.SampleText);

        var legacy = new List<byte[]>();
        using (var stream = new MemoryStream(pdf))
        {
#pragma warning disable CA1416
            foreach (var bitmap in PDFtoImage.Conversion.ToImages(stream, options: new PDFtoImage.RenderOptions { Dpi = 100 }))
#pragma warning restore CA1416
            {
                using (bitmap)
                {
                    using var image   = SKImage.FromBitmap(bitmap);
                    using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 80);
                    legacy.Add(encoded.ToArray());
                }
            }
        }

        var current = new PdfToImagePageRenderer().RenderJpegs(pdf, 100, 10);

        Assert.Equal(2, current.Count);
        Assert.Equal(legacy.Count, current.Count);
        for (var i = 0; i < legacy.Count; i++)
            Assert.Equal(legacy[i], current[i]);
    }

    [Theory]
    [InlineData(0f, 842f)]
    [InlineData(595f, -1f)]
    [InlineData(float.NaN, 842f)]
    [InlineData(595f, float.PositiveInfinity)]
    public void OptionsFor_APageWithoutAUsableSize_Throws(float width, float height)
        => Assert.Throws<InvalidOperationException>(
            () => PdfToImagePageRenderer.OptionsFor(new System.Drawing.SizeF(width, height), 200, 1568));

    [Fact]
    public void OptionsFor_ADegenerateStrip_KeepsEachSideAtLeastOnePixel()
    {
        var options = PdfToImagePageRenderer.OptionsFor(new System.Drawing.SizeF(14_400f, 1f), 200, 1568);

        Assert.Equal(1568, options.Width);
        Assert.Equal(1, options.Height);
    }

    [Fact]
    public void JsonPointer_GetSetAndEscaping()
    {
        var root = JsonNode.Parse("""{"a":{"b/c":[10,{"d~e":null}]}}""")!;

        Assert.Equal(10, LegajoJsonPointer.Get(root, "/a/b~1c/0")!.GetValue<int>());

        Assert.Null(LegajoJsonPointer.Get(root, "/a/b~1c/1/d~0e", out var found));
        Assert.True(found);                                   // an existing JSON null is found
        LegajoJsonPointer.Get(root, "/a/missing", out found);
        Assert.False(found);
        LegajoJsonPointer.Get(root, "/a/b~1c/01", out found); // leading zero is not an index
        Assert.False(found);

        Assert.True(LegajoJsonPointer.Set(root, "/a/b~1c/2", 30));   // index == count appends
        Assert.Equal(3, root["a"]!["b/c"]!.AsArray().Count);
        Assert.False(LegajoJsonPointer.Set(root, "/a/b~1c/9", 1));
        Assert.False(LegajoJsonPointer.Set(root, "/x/y", 1));

        Assert.Equal("/a/b~1c/d~0e", LegajoJsonPointer.Build(["a", "b/c", "d~e"]));
        Assert.Null(LegajoJsonPointer.Parse("a/b"));
    }
}
