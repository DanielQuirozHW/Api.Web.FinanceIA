// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-09

using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;
using iText.Kernel.Geom;
using Xunit;
using static Api.Web.FinanceIA.Tests.BalanceCorrections.BalanceSuggestionTextPdf;

namespace Api.Web.FinanceIA.Tests.BalanceCorrections;

/// <summary>
/// The suggestion's <c>textMatch</c> / <c>textMatchSecondary</c>: each verified figure found where it is PRINTED,
/// on the full upright page the Portal shows, through the REAL verifier and integrity checks over synthetic PDFs
/// rendered exactly as the suggestion's renderer does.
/// </summary>
public sealed class BalanceSuggestionTextMatchTests
{
    private static readonly IBalanceIntegrityService Integrity = new BalanceIntegrityService();

    /// <summary>
    /// The sideways balance (in thousands) as a document: a portrait page 1, and a LANDSCAPE page 2 whose right
    /// half carries the pasivo corriente — printed 150.000, its two rubros and the other totals.
    /// </summary>
    internal static byte[] SidewaysPdf() => Document(
        new PageSpec([new Run("Estado de situacion patrimonial", 60, 780), .. Filler(60, 700)]),
        new PageSpec(
        [
            new Run("Total Activo Corriente", 60, 400), new Run("200.000", 300, 400),
            new Run("Deudas comerciales", 460, 460), new Run("30.000", 720, 460),
            new Run("Deudas bancarias", 460, 440), new Run("120.000", 720, 440),
            new Run("Total Pasivo Corriente", 460, 400), new Run("150.000", 720, 400),
            new Run("Total Pasivo No Corriente", 460, 360), new Run("50.000", 720, 360),
        ], PageSize.A4.Rotate()));

    /// <summary>
    /// The images of <see cref="SidewaysPdf"/> as the request carried them: both full pages (1, 2) rendered for
    /// real, then page 2's halves (3, 4) — the model read the total from 4, the right half.
    /// </summary>
    internal static IReadOnlyList<RenderedImage> SidewaysImages(byte[] pdf)
    {
        var full = RenderAll(pdf);
        return
        [
            full[0], full[1],
            BalanceSuggestionFixtures.Image(3, 2, RenderedImage.LeftHalf, width: 640, height: 827),
            BalanceSuggestionFixtures.Image(4, 2, RenderedImage.RightHalf, width: 640, height: 827),
        ];
    }

    /// <summary>The income statement in pesos: the net result and the pre-tax subtotal on page 1, the tax on page 2.</summary>
    private static byte[] NetProfitPdf() => Document(
        new PageSpec(
        [
            new Run("Resultado antes del impuesto a las ganancias", 60, 700), new Run("12.500.000,00", 420, 700),
            new Run("Ganancia del ejercicio", 60, 650), new Run("11.861.910,77", 420, 650),
            .. Filler(60, 500),
        ]),
        new PageSpec(
        [
            new Run("Impuesto a las ganancias", 60, 500), new Run("(2.330.167,58)", 420, 500),
            .. Filler(60, 300),
        ]));

    // ── A printed figure ─────────────────────────────────────────────────────

    [Fact]
    public void A_printed_figure_read_from_a_half_is_matched_on_its_full_upright_page()
    {
        var pdf    = SidewaysPdf();
        var images = SidewaysImages(pdf);
        var plan   = BalanceSuggestionPlanner.Build(BalanceSuggestionFixtures.SidewaysBalanceJson(), 2024)!;

        using var text = new BalanceSuggestionPageText(pdf);
        var verdict = BalanceSuggestionVerifier.Verify(plan, BalanceSuggestionFixtures.SidewaysFixAnswer(), images, Integrity, text);

        Assert.Equal(BalanceSuggestionOutcomes.Fixes, verdict.Outcome);
        var total = verdict.Suggestions.Single(s => s.Path == "current_liabilities");

        // 150 000 000 pesos ÷ scale 1000 = the printed 150.000, on page 2, in the RIGHT half of the full page.
        var match = Assert.IsType<BalanceSuggestionTextMatch>(total.TextMatch);
        Assert.Equal(2, match.Page);
        Assert.InRange(match.Left, 720 / 842d - 0.01, 720 / 842d + 0.01);
        Assert.InRange(match.Bottom, (595 - 400) / 595d - 0.003, (595 - 400 + 4) / 595d);
        Assert.True(Ink(images[1], match.Top, match.Bottom, match.Left, match.Right) > 0.1);
        Assert.Null(total.TextMatchSecondary);

        // The model's band and the crop are untouched: the text match is a field beside them.
        Assert.NotNull(total.Evidence);
        Assert.NotNull(total.Highlight);

        // A line proposed to zero has no location and is never searched.
        var zero = verdict.Suggestions.Single(s => s.Path == "current_liabilities_items[2].amount");
        Assert.Null(zero.TextMatch);
        Assert.Equal(1, text.Searched);
        Assert.Equal(1, text.Found);
    }

    [Fact]
    public void Without_a_text_layer_every_text_match_is_null_and_the_rest_is_unchanged()
    {
        var pdf    = SidewaysPdf();
        var images = SidewaysImages(pdf);
        var plan   = BalanceSuggestionPlanner.Build(BalanceSuggestionFixtures.SidewaysBalanceJson(), 2024)!;

        var without = BalanceSuggestionVerifier.Verify(plan, BalanceSuggestionFixtures.SidewaysFixAnswer(), images, Integrity);
        using var text = new BalanceSuggestionPageText(pdf);
        var with = BalanceSuggestionVerifier.Verify(plan, BalanceSuggestionFixtures.SidewaysFixAnswer(), images, Integrity, text);

        Assert.All(without.Suggestions, s => Assert.Null(s.TextMatch));
        Assert.Equal(
            without.Suggestions.Select(s => s with { TextMatch = null, TextMatchSecondary = null }),
            with.Suggestions.Select(s => s with { TextMatch = null, TextMatchSecondary = null }));
    }

    [Fact]
    public void A_scanned_document_gets_no_text_match_and_no_error()
    {
        var scan   = Scan();
        var images = SidewaysImages(SidewaysPdf());
        var plan   = BalanceSuggestionPlanner.Build(BalanceSuggestionFixtures.SidewaysBalanceJson(), 2024)!;

        using var text = new BalanceSuggestionPageText(scan);
        var verdict = BalanceSuggestionVerifier.Verify(plan, BalanceSuggestionFixtures.SidewaysFixAnswer(), images, Integrity, text);

        Assert.Equal(BalanceSuggestionOutcomes.Fixes, verdict.Outcome);
        Assert.All(verdict.Suggestions, s => Assert.Null(s.TextMatch));
    }

    // ── A derived figure ("Calculado") ───────────────────────────────────────

    [Fact]
    public void A_derived_figure_matches_the_net_result_and_the_tax_it_was_calculated_from()
    {
        var pdf    = NetProfitPdf();
        var images = RenderAll(pdf);
        var plan   = BalanceSuggestionPlanner.Build(BalanceSuggestionFixtures.NetProfitBalanceJson(), 2024)!;

        using var text = new BalanceSuggestionPageText(pdf);
        var verdict = BalanceSuggestionVerifier.Verify(plan, BalanceSuggestionFixtures.NetProfitAnswer(), images, Integrity, text);

        var item = Assert.Single(verdict.Suggestions);
        Assert.Equal(BalanceSuggestionBases.Derived, item.Basis);

        // The net result (11.861.910,77) on page 1's second row — not the pre-tax subtotal above it.
        var primary = Assert.IsType<BalanceSuggestionTextMatch>(item.TextMatch);
        Assert.Equal(1, primary.Page);
        Assert.InRange(primary.Bottom, (842 - 650) / 842d - 0.003, (842 - 650 + 4) / 842d);
        Assert.True(Ink(images[0], primary.Top, primary.Bottom, primary.Left, primary.Right) > 0.1);

        // The tax, stored negative and printed in parentheses, on page 2.
        var secondary = Assert.IsType<BalanceSuggestionTextMatch>(item.TextMatchSecondary);
        Assert.Equal(2, secondary.Page);
        Assert.InRange(secondary.Bottom, (842 - 500) / 842d - 0.003, (842 - 500 + 4) / 842d);
        Assert.True(Ink(images[1], secondary.Top, secondary.Bottom, secondary.Left, secondary.Right) > 0.1);
    }

    [Fact]
    public void A_figure_not_printed_on_the_page_the_model_named_gets_no_match()
    {
        // The tax is printed on page 1 here, but the model located it on page 2: only that page is searched.
        var pdf = Document(
            new PageSpec(
            [
                new Run("Resultado antes del impuesto a las ganancias", 60, 700), new Run("12.500.000,00", 420, 700),
                new Run("Ganancia del ejercicio", 60, 650), new Run("11.861.910,77", 420, 650),
                new Run("Impuesto a las ganancias", 60, 600), new Run("(2.330.167,58)", 420, 600),
                .. Filler(60, 500),
            ]),
            new PageSpec([.. Filler(60, 500), new Run("Notas a los estados contables", 60, 300)]));
        var images = RenderAll(pdf);
        var plan   = BalanceSuggestionPlanner.Build(BalanceSuggestionFixtures.NetProfitBalanceJson(), 2024)!;

        using var text = new BalanceSuggestionPageText(pdf);
        var item = Assert.Single(
            BalanceSuggestionVerifier.Verify(plan, BalanceSuggestionFixtures.NetProfitAnswer(), images, Integrity, text).Suggestions);

        Assert.NotNull(item.TextMatch);
        Assert.Null(item.TextMatchSecondary);
    }

    // ── The contract ─────────────────────────────────────────────────────────

    [Fact]
    public void The_item_serialises_textMatch_and_textMatchSecondary_beside_every_existing_field()
    {
        var item = new BalanceCorrectionSuggestionItem
        {
            Path           = "current_liabilities",
            SuggestedValue = 150_000_000d,
            Page           = 2,
            TextMatch      = new BalanceSuggestionTextMatch(2, 0.25, 0.27, 0.85, 0.92),
            SecondaryPage  = 3,
        };

        var json = JsonNode.Parse(JsonSerializer.Serialize(item, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!.AsObject();

        Assert.Equal(
            ["path", "currentValue", "suggestedValue", "printedLabel", "page", "evidence", "highlight", "basis",
             "derivation", "evidenceSecondary", "textMatch", "textMatchSecondary"],
            json.Select(p => p.Key));
        Assert.Equal(
            """{"page":2,"top":0.25,"bottom":0.27,"left":0.85,"right":0.92}""",
            json["textMatch"]!.ToJsonString());
        Assert.Null(json["textMatchSecondary"]);
    }
}
