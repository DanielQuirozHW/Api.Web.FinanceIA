// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-09

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;
using Xunit;
using static Api.Web.FinanceIA.Tests.BalanceCorrections.BalanceSuggestionTextPdf;

namespace Api.Web.FinanceIA.Tests.BalanceCorrections;

/// <summary>
/// Pins <see cref="BalanceSuggestionPageText"/>: a suggested figure found where it is PRINTED, on synthetic
/// pages with known positions. Where the frame matters (a turned page), the page is rendered exactly as the
/// suggestion's renderer does and the returned rectangle must hold the figure's INK — the mapping is checked
/// against PDFium, not only against arithmetic.
/// </summary>
public sealed class BalanceSuggestionPageTextTests
{
    private const string Label = "Total del pasivo corriente";

    // A4 portrait, in points.
    private const double A4Width  = 595d;
    private const double A4Height = 842d;

    private static BalanceSuggestionTextMatch? Find(
        byte[] pdf, double printedValue, string? label = null, RenderedImage? image = null)
    {
        using var text = new BalanceSuggestionPageText(pdf);
        return text.Find(1, printedValue, label, image ?? Render(pdf));
    }

    private static byte[] Statement(params Run[] runs) => Page(runs.Concat(Filler(60, 400)));

    // ── Plain figures ────────────────────────────────────────────────────────

    [Fact]
    public void A_plain_figure_is_found_where_it_is_printed_in_top_left_fractions()
    {
        var pdf   = Statement(new Run(Label, 60, 700), new Run("1.234.567,89", 420, 700));
        var image = Render(pdf);

        var match = Find(pdf, 1_234_567.89, image: image);

        Assert.NotNull(match);
        Assert.Equal(1, match.Page);
        Assert.InRange(match.Left, 420 / A4Width - 0.01, 420 / A4Width + 0.01);
        Assert.InRange(match.Right, (420 + Width("1.234.567,89")) / A4Width - 0.01, (420 + Width("1.234.567,89")) / A4Width + 0.01);
        Assert.InRange(match.Bottom, (A4Height - 700) / A4Height - 0.002, (A4Height - 700 + 4) / A4Height);
        Assert.InRange(match.Top, (A4Height - 700 - FontSize) / A4Height, (A4Height - 700) / A4Height);
        Assert.True(match.Top < match.Bottom && match.Left < match.Right);
        Assert.True(Ink(image, match.Top, match.Bottom, match.Left, match.Right) > 0.1);
    }

    [Theory]
    [InlineData("1.234.567", 1_234_567d)]
    [InlineData("1234567", 1_234_567d)]
    [InlineData("$ 1.234.567,89", 1_234_567.89)]
    [InlineData("$1.234.567,89", 1_234_567.89)]
    public void Every_es_AR_spelling_of_the_figure_matches(string printed, double value)
    {
        var pdf = Statement(new Run(Label, 60, 700), new Run(printed, 420, 700));

        Assert.NotNull(Find(pdf, value));
    }

    [Fact]
    public void A_document_in_thousands_matches_the_figure_in_printed_units()
    {
        // 1 234 000 pesos in a document "expresado en miles": printed 1.234, searched as pesos ÷ scale.
        var pdf = Statement(new Run(Label, 60, 700), new Run("1.234", 420, 700));

        Assert.NotNull(Find(pdf, 1_234_000d / 1_000d));
        Assert.Null(Find(pdf, 1_234_000d));
    }

    [Theory]
    [InlineData("(45.678)", -45_678d)]
    [InlineData("(45.678)", 45_678d)]
    [InlineData("-45.678", -45_678d)]
    [InlineData("−45.678", 45_678d)]
    [InlineData("$ (45.678,00)", -45_678d)]
    public void A_negative_matches_by_magnitude_in_parentheses_or_with_a_minus(string printed, double value)
    {
        var pdf = Statement(new Run("Impuesto a las ganancias", 60, 700), new Run(printed, 420, 700));

        Assert.NotNull(Find(pdf, value));
    }

    [Fact]
    public void A_figure_written_as_two_text_runs_is_one_match_spanning_both()
    {
        var pdf = Statement(
            new Run(Label, 60, 700),
            new Run("1.234.", 420, 700),
            new Run("567", 420 + Width("1.234."), 700));

        var match = Find(pdf, 1_234_567d);

        Assert.NotNull(match);
        Assert.InRange(match.Left, 420 / A4Width - 0.01, 420 / A4Width + 0.01);
        Assert.True(match.Right >= (420 + Width("1.234.567")) / A4Width - 0.01);
    }

    [Fact]
    public void A_figure_that_is_only_part_of_a_longer_number_does_not_match()
    {
        var pdf = Statement(new Run(Label, 60, 700), new Run("11.234.567", 420, 700), new Run("1.234.5678", 420, 650));

        Assert.Null(Find(pdf, 1_234_567d));
        Assert.Null(Find(pdf, 234_567d));
    }

    // ── Ambiguity ────────────────────────────────────────────────────────────

    [Fact]
    public void Two_equal_figures_with_no_label_are_ambiguous()
    {
        var pdf = Statement(
            new Run(Label, 60, 700), new Run("150.000", 420, 700),
            new Run("Total del activo corriente", 60, 650), new Run("150.000", 420, 650));

        Assert.Null(Find(pdf, 150_000d));
        Assert.Null(Find(pdf, 150_000d, "Otra partida"));
    }

    [Fact]
    public void Two_equal_figures_resolve_to_the_one_on_the_label_row()
    {
        var pdf = Statement(
            new Run("Total del activo corriente", 60, 700), new Run("150.000", 420, 700),
            new Run(Label, 60, 650), new Run("150.000", 420, 650));

        var match = Find(pdf, 150_000d, "TOTAL DEL PASIVO CORRIENTE:");

        Assert.NotNull(match);
        Assert.InRange(match.Bottom, (A4Height - 650) / A4Height - 0.002, (A4Height - 650 + 4) / A4Height);
    }

    [Fact]
    public void Two_equal_figures_on_the_label_row_stay_ambiguous()
    {
        // Current and prior year printing the same figure: no way to tell the columns apart from the text.
        var pdf = Statement(new Run(Label, 60, 700), new Run("150.000", 360, 700), new Run("150.000", 460, 700));

        Assert.Null(Find(pdf, 150_000d, Label));
    }

    [Fact]
    public void A_single_candidate_counts_even_when_the_label_is_elsewhere()
    {
        var pdf = Statement(new Run(Label, 60, 700), new Run("150.000", 420, 650));

        Assert.NotNull(Find(pdf, 150_000d, Label));
    }

    // ── Turned pages: the frame is the image the renderer sends ─────────────────

    [Fact]
    public void Text_running_up_an_unrotated_page_is_matched_on_the_upright_image()
    {
        var pdf = Page(
            [new Run(Label, 100, 100), new Run("1.234.567", 100, 500), .. Filler(200, 100, Direction.RunningUp)],
            Direction.RunningUp);
        Assert.Equal([90], PdfPageOrientation.DetectUprightRotations(pdf, 1));

        var image = Render(pdf);
        Assert.True(image.Width > image.Height);   // turned: landscape

        var match = Find(pdf, 1_234_567d, image: image);

        AssertInkAndNotMirrored(image, match);
    }

    [Fact]
    public void Upright_text_on_a_rotate_90_page_is_matched_on_the_upright_image()
    {
        var pdf = Page([new Run(Label, 60, 700), new Run("1.234.567", 420, 700), .. Filler(60, 400)], pageRotate: 90);
        Assert.Equal([270], PdfPageOrientation.DetectUprightRotations(pdf, 1));

        var image = Render(pdf);
        Assert.True(image.Height > image.Width);   // displayed landscape, turned back to portrait

        var match = Find(pdf, 1_234_567d, image: image);

        AssertInkAndNotMirrored(image, match);
        Assert.InRange(match!.Left, 420 / A4Width - 0.01, 420 / A4Width + 0.01);
    }

    [Fact]
    public void An_unrotated_page_is_matched_on_its_ink_too()
    {
        var pdf   = Statement(new Run(Label, 60, 700), new Run("1.234.567", 420, 700));
        var image = Render(pdf);

        AssertInkAndNotMirrored(image, Find(pdf, 1_234_567d, image: image));
    }

    // ── No match, never an error ─────────────────────────────────────────────

    [Fact]
    public void A_scan_has_no_text_layer_and_no_match()
    {
        var pdf = Scan();

        using var text = new BalanceSuggestionPageText(pdf);
        Assert.Null(text.Find(1, 150_000d, Label, Render(pdf)));
        Assert.Equal(1, text.Searched);
        Assert.Equal(0, text.Found);
    }

    [Fact]
    public void Unreadable_bytes_give_no_match_and_report_the_exception_type_only()
    {
        var failures = new List<string>();
        using var text = new BalanceSuggestionPageText("not a pdf at all"u8.ToArray(), failures.Add);

        Assert.Null(text.Find(1, 150_000d, Label, BalanceSuggestionFixtures.Image(1, 1, RenderedImage.FullHalf)));
        Assert.Null(text.Find(1, 150_000d, Label, BalanceSuggestionFixtures.Image(1, 1, RenderedImage.FullHalf)));
        Assert.Single(failures);
    }

    [Fact]
    public void An_image_whose_aspect_is_not_the_upright_page_gives_no_match()
    {
        var pdf     = Statement(new Run(Label, 60, 700), new Run("1.234.567", 420, 700));
        var image   = Render(pdf);
        var swapped = image with { Width = image.Height, Height = image.Width };

        Assert.NotNull(Find(pdf, 1_234_567d, image: image));
        Assert.Null(Find(pdf, 1_234_567d, image: swapped));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Zero_or_a_non_finite_figure_is_never_searched(double value)
    {
        var pdf = Statement(new Run(Label, 60, 700), new Run("0", 420, 700));

        using var text = new BalanceSuggestionPageText(pdf);
        Assert.Null(text.Find(1, value, Label, Render(pdf)));
        Assert.Equal(0, text.Searched);
    }

    [Fact]
    public void A_page_beyond_the_document_or_no_image_gives_no_match()
    {
        var pdf   = Statement(new Run(Label, 60, 700), new Run("1.234.567", 420, 700));
        var image = Render(pdf);

        using var text = new BalanceSuggestionPageText(pdf);
        Assert.Null(text.Find(2, 1_234_567d, Label, image));
        Assert.Null(text.Find(0, 1_234_567d, Label, image));
        Assert.Null(text.Find(1, 1_234_567d, Label, null));
        Assert.NotNull(text.Find(1, 1_234_567d, Label, image));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("TOTAL PASIVO CORRIENTE:", "total pasivo corriente")]
    [InlineData("  Créditos   por ventas (Nota 3) ", "creditos por ventas nota 3")]
    [InlineData("—", null)]
    [InlineData(null, null)]
    public void Normalise_drops_accents_case_and_punctuation(string? text, string? expected) =>
        Assert.Equal(expected, BalanceSuggestionPageText.Normalise(text));

    [Theory]
    [InlineData("1.234.567", "89", 1_234_567.89)]
    [InlineData("1234567", null, 1_234_567d)]
    [InlineData("1.234", null, 1_234d)]
    public void ParseAmount_reads_es_AR_thousands_and_decimals(string integerPart, string? decimals, double expected) =>
        Assert.Equal((decimal)expected, BalanceSuggestionPageText.ParseAmount(integerPart, decimals));

    // The match must hold the figure's ink on the image the renderer produced, and the rectangle mirrored through
    // the page's centre (where a wrong turn or a flipped axis would put it) must be blank.
    private static void AssertInkAndNotMirrored(RenderedImage image, BalanceSuggestionTextMatch? match)
    {
        Assert.NotNull(match);
        Assert.True(Ink(image, match.Top, match.Bottom, match.Left, match.Right) > 0.1,
            "the matched rectangle holds no ink");
        Assert.Equal(0d, Ink(image, 1 - match.Bottom, 1 - match.Top, 1 - match.Right, 1 - match.Left));
    }
}
