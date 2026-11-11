// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;
using SkiaSharp;
using Xunit;

namespace Api.Web.FinanceIA.Tests.BalanceCorrections;

/// <summary>
/// The full pages of a suggestion (<see cref="BalanceSuggestionEvidence.Pages"/>): one entry per distinct
/// page the suggestions refer to, the upright FULL image (never a half), downscaled and size-capped.
/// </summary>
public sealed class BalanceSuggestionPagesTests
{
    [Fact]
    public void One_entry_per_distinct_page_in_ascending_order()
    {
        var images = BalanceSuggestionFixtures.SidewaysImages();

        var pages = BalanceSuggestionEvidence.Pages([Item(2), Item(1), Item(2), Item(null)], images);

        Assert.Equal([1, 2], pages.Select(p => p.Page));
        Assert.All(pages, p => Assert.Equal(BalanceSuggestionEvidence.MediaType, p.Image.MediaType));
    }

    [Fact]
    public void A_page_read_by_halves_returns_its_full_upright_image()
    {
        var images = BalanceSuggestionFixtures.SidewaysImages();

        var page = Assert.Single(BalanceSuggestionEvidence.Pages([Item(2)], images));

        using var bitmap = SKBitmap.Decode(Convert.FromBase64String(page.Image.Data));
        Assert.Equal(1400, bitmap.Width);   // the full page, not a 784 px half
        Assert.Equal(1000, bitmap.Height);
    }

    [Fact]
    public void Pages_no_suggestion_refers_to_are_left_out()
    {
        Assert.Empty(BalanceSuggestionEvidence.Pages([Item(null)], BalanceSuggestionFixtures.SidewaysImages()));
        Assert.Empty(BalanceSuggestionEvidence.Pages([Item(7)], BalanceSuggestionFixtures.SidewaysImages()));
        Assert.Empty(BalanceSuggestionEvidence.Pages([], BalanceSuggestionFixtures.SidewaysImages()));
    }

    [Fact]
    public void A_large_noisy_page_is_downscaled_to_1600_px_and_held_under_450_kb()
    {
        var big = Noise(1, 1, 3000, 2000);

        var page = Assert.Single(BalanceSuggestionEvidence.Pages([Item(1)], [big]));

        var bytes = Convert.FromBase64String(page.Image.Data);
        Assert.True(bytes.Length <= BalanceSuggestionEvidence.PageMaxBytes, $"{bytes.Length} bytes");
        using var bitmap = SKBitmap.Decode(bytes);
        Assert.True(Math.Max(bitmap.Width, bitmap.Height) <= BalanceSuggestionEvidence.PageMaxSidePx);
        Assert.Equal(1.5, (double)bitmap.Width / bitmap.Height, 2);   // the aspect ratio is kept
    }

    [Fact]
    public void A_small_page_is_never_upscaled()
    {
        var page = BalanceSuggestionEvidence.PageImage(BalanceSuggestionFixtures.Image(1, 1, RenderedImage.FullHalf))!;

        using var bitmap = SKBitmap.Decode(Convert.FromBase64String(page.Data));
        Assert.Equal(600, bitmap.Width);
        Assert.Equal(1000, bitmap.Height);
    }

    [Fact]
    public void An_undecodable_page_is_left_out()
    {
        var broken = new RenderedImage(1, 1, RenderedImage.FullHalf, 600, 1000, "not base64!");

        Assert.Empty(BalanceSuggestionEvidence.Pages([Item(1)], [broken]));
    }

    private static BalanceCorrectionSuggestionItem Item(int? page) => new()
    {
        Path = "current_liabilities", SuggestedValue = 1d, Page = page,
    };

    // Random pixels: the worst case for JPEG, so the size cap has to work for it.
    private static RenderedImage Noise(int number, int page, int width, int height)
    {
        var random = new Random(42);
        using var bitmap = new SKBitmap(width, height);
        var pixels = new SKColor[width * height];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        bitmap.Pixels = pixels;

        using var image = SKImage.FromBitmap(bitmap);
        using var data  = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return new RenderedImage(number, page, RenderedImage.FullHalf, width, height, Convert.ToBase64String(data.ToArray()));
    }
}
