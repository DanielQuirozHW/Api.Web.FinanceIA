// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;
using SkiaSharp;
using Xunit;

namespace Api.Web.FinanceIA.Tests.BalanceCorrections;

/// <summary>
/// The pure halves of the correction suggestion (10-F4a): what is asked (<see cref="BalanceSuggestionPlanner"/>),
/// how the answer is verified (<see cref="BalanceSuggestionVerifier"/>, over the REAL integrity checks) and
/// the evidence crop (<see cref="BalanceSuggestionEvidence"/>). Synthetic balances only.
/// </summary>
public sealed class BalanceSuggestionVerifierTests
{
    private static readonly IBalanceIntegrityService Integrity = new BalanceIntegrityService();

    // ── Planner ─────────────────────────────────────────────────────────────────

    [Fact]
    public void The_plan_asks_the_figures_of_the_failed_structure_checks_in_printed_units()
    {
        var plan = BalanceSuggestionPlanner.Build(BalanceSuggestionFixtures.SidewaysBalanceJson(), 2024)!;

        Assert.Equal([BalanceIntegrityService.LiabilitiesComponentsCheck, BalanceIntegrityService.CurrentLiabilitiesItemsCheck],
            plan.FailedChecks);
        Assert.Equal(1000d, plan.Scale);
        Assert.Equal(["liabilities", "current_liabilities", "non_current_liabilities"],
            plan.Question.Fields.Select(f => f.Path));
        Assert.Equal(200_000d, plan.Question.Fields.Single(f => f.Path == "current_liabilities").StoredPrinted);
        Assert.Equal("Total Pasivo Corriente", plan.Question.Fields.Single(f => f.Path == "current_liabilities").LabelHint);

        var group = Assert.Single(plan.Question.ItemGroups);
        Assert.Equal("current_liabilities_items", group.Array);
        Assert.Equal([30_000d, 120_000d, 40_000d], group.StoredPrinted.Select(v => v!.Value));

        // Stored values in pesos, for the verification.
        Assert.Equal(BalanceSuggestionFixtures.MisreadCurrentLiabilities, plan.StoredByPath["current_liabilities"]);
        Assert.Equal(40_000_000d, plan.StoredByPath["current_liabilities_items[2].amount"]);
        Assert.Equal(2024, plan.Question.FiscalYear);
    }

    [Fact]
    public void The_finex_plan_asks_the_subtotal_and_every_rubro_line()
    {
        var plan = BalanceSuggestionPlanner.Build(BalanceCorrectionFixtures.FinexBalanceJson(), 2024)!;

        Assert.Equal([BalanceIntegrityService.CurrentAssetsItemsCheck], plan.FailedChecks);
        Assert.Equal(["current_assets"], plan.Question.Fields.Select(f => f.Path));
        Assert.Equal(4, Assert.Single(plan.Question.ItemGroups).StoredPrinted.Count);
    }

    [Theory]
    [InlineData("passed")]
    [InlineData("only_soft_check")]
    [InlineData("integrity_null")]
    [InlineData("no_integrity")]
    [InlineData("unreadable")]
    public void There_is_no_plan_without_a_failed_targeted_check(string shape)
    {
        var json = shape switch
        {
            "passed"          => BalanceCorrectionFixtures.CorrectedFigures(),
            "only_soft_check" => BalanceSuggestionFixtures.SignOnlyBalanceJson(),   // sign_net_revenue only (not targeted)
            "integrity_null"  => WithNode(BalanceCorrectionFixtures.FinexBalanceJson(), "integrity", null),
            "no_integrity"    => Without(BalanceCorrectionFixtures.FinexBalanceJson(), "integrity"),
            _                 => "not json",
        };

        Assert.Null(BalanceSuggestionPlanner.Build(json, 2024));
    }

    // ── Verifier: outcomes ──────────────────────────────────────────────────────

    [Fact]
    public void Finex_the_dash_read_as_a_figure_proposed_as_zero_fixes_the_balance()
    {
        var plan    = BalanceSuggestionPlanner.Build(BalanceCorrectionFixtures.FinexBalanceJson(), 2024)!;
        var images  = new[] { BalanceSuggestionFixtures.Image(1, 2, RenderedImage.FullHalf) };

        var verdict = BalanceSuggestionVerifier.Verify(plan, BalanceSuggestionFixtures.FinexFixAnswer(), images, Integrity);

        Assert.Equal(BalanceSuggestionOutcomes.Fixes, verdict.Outcome);
        Assert.Equal(BalanceSuggestionVerifier.FixesMessage, verdict.Message);
        Assert.Equal([BalanceIntegrityService.CurrentAssetsItemsCheck], verdict.FailedChecksBefore);
        Assert.Empty(verdict.FailedChecksAfter);

        var suggestion = Assert.Single(verdict.Suggestions);
        Assert.Equal(BalanceCorrectionFixtures.MisreadPath, suggestion.Path);
        Assert.Equal(BalanceCorrectionFixtures.MisreadAmount, suggestion.CurrentValue);
        Assert.Equal(0d, suggestion.SuggestedValue);
        Assert.Equal("Créditos por ventas", suggestion.PrintedLabel);
        Assert.Equal(2, suggestion.Page);
        Assert.NotNull(suggestion.Evidence);
        Assert.Equal("image/jpeg", suggestion.Evidence!.MediaType);
        Assert.Equal(new BalanceSuggestionHighlight(2, 0.25, 0.28, 0d, 1d), suggestion.Highlight);
    }

    [Fact]
    public void Sideways_scan_the_total_and_the_extra_rubro_are_proposed_and_fix_every_check()
    {
        var plan = BalanceSuggestionPlanner.Build(BalanceSuggestionFixtures.SidewaysBalanceJson(), 2024)!;

        var verdict = BalanceSuggestionVerifier.Verify(
            plan, BalanceSuggestionFixtures.SidewaysFixAnswer(), BalanceSuggestionFixtures.SidewaysImages(), Integrity);

        Assert.Equal(BalanceSuggestionOutcomes.Fixes, verdict.Outcome);
        Assert.Empty(verdict.FailedChecksAfter);
        Assert.Equal(["current_liabilities", "current_liabilities_items[2].amount"], verdict.Suggestions.Select(s => s.Path));

        // amounts_scale respected: 150.000 printed "en miles" is 150.000.000 pesos.
        var total = verdict.Suggestions[0];
        Assert.Equal(150_000_000d, total.SuggestedValue);
        Assert.Equal(BalanceSuggestionFixtures.MisreadCurrentLiabilities, total.CurrentValue);
        Assert.Equal(2, total.Page);
        Assert.NotNull(total.Evidence);

        // The line no printed line stands for goes to zero, with no location.
        var extra = verdict.Suggestions[1];
        Assert.Equal(0d, extra.SuggestedValue);
        Assert.Equal(40_000_000d, extra.CurrentValue);
        Assert.Null(extra.PrintedLabel);
        Assert.Null(extra.Page);
        Assert.Null(extra.Evidence);

        Assert.Equal(4, verdict.Unchanged);   // liabilities, non_current_liabilities and rubros 0 and 1 confirmed
    }

    [Fact]
    public void A_proposal_that_breaks_another_check_is_not_a_fix()
    {
        var plan   = BalanceSuggestionPlanner.Build(BalanceCorrectionFixtures.FinexBalanceJson(), 2024)!;
        var answer = BalanceSuggestionFixtures.FinexFixAnswer();
        answer.Fields![0].PrintedValue = 250_000;   // a wrong activo corriente: assets_components now fails

        var verdict = BalanceSuggestionVerifier.Verify(plan, answer, [], Integrity);

        Assert.Equal(BalanceSuggestionOutcomes.NoSuggestion, verdict.Outcome);
        Assert.Empty(verdict.Suggestions);
        Assert.Equal(BalanceSuggestionVerifier.NoSuggestionMessage, verdict.Message);
        Assert.Contains(BalanceIntegrityService.AssetsComponentsCheck, verdict.FailedChecksAfter);
    }

    [Fact]
    public void A_proposal_that_fixes_one_of_two_failed_checks_reduces()
    {
        var plan   = BalanceSuggestionPlanner.Build(BalanceSuggestionFixtures.SidewaysBalanceJson(), 2024)!;
        var answer = BalanceSuggestionFixtures.SidewaysFixAnswer();
        // The model claims the extra stored line as printed: the rubros still add up to 190.000, not 150.000.
        answer.Items![0].Lines!.Add(new BalanceSuggestionLineAnswer { StoredIndex = 2, PrintedValue = 40_000, MatchesStored = true });

        var verdict = BalanceSuggestionVerifier.Verify(plan, answer, BalanceSuggestionFixtures.SidewaysImages(), Integrity);

        Assert.Equal(BalanceSuggestionOutcomes.Reduces, verdict.Outcome);
        Assert.Equal([BalanceIntegrityService.CurrentLiabilitiesItemsCheck], verdict.FailedChecksAfter);
        Assert.Equal(["current_liabilities"], verdict.Suggestions.Select(s => s.Path));
        Assert.Contains("1 de los 2 controles", verdict.Message);
    }

    [Fact]
    public void Every_figure_confirmed_as_stored_while_the_checks_fail_is_a_document_error()
    {
        var plan = BalanceSuggestionPlanner.Build(BalanceCorrectionFixtures.FinexBalanceJson(), 2024)!;

        var verdict = BalanceSuggestionVerifier.Verify(plan, BalanceSuggestionFixtures.FinexConfirmedAnswer(), [], Integrity);

        Assert.Equal(BalanceSuggestionOutcomes.DocumentError, verdict.Outcome);
        Assert.Empty(verdict.Suggestions);
        Assert.StartsWith("El balance presentado no cierra: la suma de los rubros del activo corriente", verdict.Message);
        Assert.Equal(verdict.FailedChecksBefore, verdict.FailedChecksAfter);
    }

    [Fact]
    public void A_figure_not_found_is_never_a_document_error()
    {
        var plan   = BalanceSuggestionPlanner.Build(BalanceCorrectionFixtures.FinexBalanceJson(), 2024)!;
        var answer = BalanceSuggestionFixtures.FinexConfirmedAnswer();
        answer.Fields![0].PrintedValue = null;

        var verdict = BalanceSuggestionVerifier.Verify(plan, answer, [], Integrity);

        Assert.Equal(BalanceSuggestionOutcomes.NoSuggestion, verdict.Outcome);
    }

    // ── Verifier: what is dropped ───────────────────────────────────────────────

    [Fact]
    public void Non_whitelisted_not_asked_unchanged_and_out_of_range_proposals_are_dropped()
    {
        var plan   = BalanceSuggestionPlanner.Build(BalanceCorrectionFixtures.FinexBalanceJson(), 2024)!;
        var answer = BalanceSuggestionFixtures.FinexFixAnswer();
        answer.Fields!.Add(new BalanceSuggestionFieldAnswer { Path = "cash_flow.net_change", PrintedValue = 1 });       // not whitelisted
        answer.Fields!.Add(new BalanceSuggestionFieldAnswer { Path = "equity", PrintedValue = 1 });                     // whitelisted, not asked
        answer.Fields!.Add(new BalanceSuggestionFieldAnswer { Path = "current_assets_items[1].amount", PrintedValue = 9 }); // an item as a field
        answer.Items![0].Lines!.Add(new BalanceSuggestionLineAnswer { StoredIndex = 9, PrintedValue = 5 });           // no such line
        answer.Items![0].Lines![2].PrintedValue = 1e300;                                                              // out of range

        var verdict = BalanceSuggestionVerifier.Verify(plan, answer, [], Integrity);

        Assert.Equal(4, verdict.DroppedOutside);
        Assert.Equal(1, verdict.DroppedInvalid);
        Assert.Equal(3, verdict.Unchanged);   // current_assets, rubro 0, rubro 3
        Assert.Equal(1, verdict.Proposed);    // rubro 1 → 0 only
        Assert.All(verdict.Suggestions, s => Assert.NotNull(BalanceCorrectionPath.TryParse(s.Path)));
        Assert.Equal(BalanceSuggestionOutcomes.Fixes, verdict.Outcome);
    }

    [Fact]
    public void A_printed_rubro_matched_to_no_stored_line_is_counted_and_proposes_nothing()
    {
        var plan   = BalanceSuggestionPlanner.Build(BalanceCorrectionFixtures.FinexBalanceJson(), 2024)!;
        var answer = BalanceSuggestionFixtures.FinexConfirmedAnswer();
        answer.Items![0].Lines!.Add(new BalanceSuggestionLineAnswer { StoredIndex = null, PrintedValue = 1_000, Label = "Otro" });

        var verdict = BalanceSuggestionVerifier.Verify(plan, answer, [], Integrity);

        Assert.Equal(1, verdict.UnmatchedPrinted);
        Assert.Equal(BalanceSuggestionOutcomes.NoSuggestion, verdict.Outcome);   // a missed line is not a document error
    }

    [Fact]
    public void A_location_whose_half_contradicts_the_image_map_gets_no_page_no_evidence_and_no_highlight()
    {
        var plan   = BalanceSuggestionPlanner.Build(BalanceCorrectionFixtures.FinexBalanceJson(), 2024)!;
        var answer = BalanceSuggestionFixtures.FinexFixAnswer();
        answer.Items![0].Lines![1].Half = "left";   // image 1 is a full page

        var verdict = BalanceSuggestionVerifier.Verify(
            plan, answer, [BalanceSuggestionFixtures.Image(1, 1, RenderedImage.FullHalf)], Integrity);

        var suggestion = Assert.Single(verdict.Suggestions);
        Assert.Null(suggestion.Page);
        Assert.Null(suggestion.Evidence);
        Assert.Null(suggestion.Highlight);
        Assert.Equal("Créditos por ventas", suggestion.PrintedLabel);
    }

    [Fact]
    public void A_long_printed_label_is_cut_to_80_characters()
    {
        var plan   = BalanceSuggestionPlanner.Build(BalanceCorrectionFixtures.FinexBalanceJson(), 2024)!;
        var answer = BalanceSuggestionFixtures.FinexFixAnswer();
        answer.Items![0].Lines![1].Label = new string('x', 200);

        var verdict = BalanceSuggestionVerifier.Verify(plan, answer, [], Integrity);

        Assert.Equal(80, Assert.Single(verdict.Suggestions).PrintedLabel!.Length);
    }

    // ── Evidence ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_crop_rectangle_is_the_band_padded_by_seven_percent_at_full_width()
    {
        var rect = BalanceSuggestionEvidence.CropRect(600, 1000, 0.40, 0.44)!.Value;

        Assert.Equal(0, rect.Left);
        Assert.Equal(600, rect.Right);
        Assert.Equal(330, rect.Top);      // (0.40 − 0.07) × 1000
        Assert.Equal(510, rect.Bottom);   // (0.44 + 0.07) × 1000
    }

    [Fact]
    public void The_widened_crop_is_clamped_at_each_edge_separately()
    {
        var top = BalanceSuggestionEvidence.CropRect(600, 1000, 0.03, 0.06)!.Value;
        Assert.Equal(0, top.Top);
        Assert.Equal(130, top.Bottom);

        var bottom = BalanceSuggestionEvidence.CropRect(600, 1000, 0.95, 0.98)!.Value;
        Assert.Equal((int)Math.Floor((0.95 - BalanceSuggestionEvidence.PaddingShare) * 1000), bottom.Top);
        Assert.Equal(1000, bottom.Bottom);
    }

    // ── Highlight: the band as a rectangle on the full page ────────────────────

    [Fact]
    public void A_band_read_on_the_full_page_spans_its_whole_width()
    {
        var images = BalanceSuggestionFixtures.SidewaysImages();

        var highlight = BalanceSuggestionEvidence.Highlight(images[1], images, 0.40, 0.44)!;

        Assert.Equal(new BalanceSuggestionHighlight(2, 0.40, 0.44, 0d, 1d), highlight);
    }

    [Fact]
    public void A_band_read_on_the_left_half_spans_the_left_halfs_x_range()
    {
        var images = BalanceSuggestionFixtures.SidewaysImages();
        var (left, _) = ClaudeService.HalfRects(1400, 1000);

        var highlight = BalanceSuggestionEvidence.Highlight(images[2], images, 0.20, 0.25)!;

        Assert.Equal(2, highlight.Page);
        Assert.Equal(0.20, highlight.Top);
        Assert.Equal(0.25, highlight.Bottom);
        Assert.Equal(0d, highlight.Left);
        Assert.Equal((double)left.Right / 1400, highlight.Right, 9);
        Assert.Equal(0.56, highlight.Right, 3);   // 6% past the midline
    }

    [Fact]
    public void A_band_read_on_the_right_half_spans_the_right_halfs_x_range()
    {
        var images = BalanceSuggestionFixtures.SidewaysImages();
        var (_, right) = ClaudeService.HalfRects(1400, 1000);

        var highlight = BalanceSuggestionEvidence.Highlight(images[3], images, 0.70, 0.74)!;

        Assert.Equal((double)right.Left / 1400, highlight.Left, 9);
        Assert.Equal(0.44, highlight.Left, 3);
        Assert.Equal(1d, highlight.Right);
        Assert.Equal(0.70, highlight.Top);
        Assert.Equal(0.74, highlight.Bottom);
    }

    [Theory]
    [InlineData(null, 0.5)]
    [InlineData(0.6, 0.5)]
    [InlineData(double.NaN, 0.5)]
    [InlineData(0.5, 1.1)]
    public void A_bad_band_has_no_highlight(double? top, double? bottom)
    {
        var images = BalanceSuggestionFixtures.SidewaysImages();

        Assert.Null(BalanceSuggestionEvidence.Highlight(images[3], images, top, bottom));
    }

    [Fact]
    public void A_half_whose_full_page_is_missing_has_no_highlight()
    {
        var half = BalanceSuggestionFixtures.Image(3, 2, RenderedImage.RightHalf, width: 784, height: 1000);

        Assert.Null(BalanceSuggestionEvidence.Highlight(half, [half], 0.40, 0.44));
    }

    [Fact]
    public void The_crop_rectangle_is_clamped_to_the_image()
    {
        var rect = BalanceSuggestionEvidence.CropRect(600, 1000, 0.01, 0.99)!.Value;

        Assert.Equal(0, rect.Top);
        Assert.Equal(1000, rect.Bottom);
    }

    [Theory]
    [InlineData(null, 0.5)]
    [InlineData(0.5, null)]
    [InlineData(0.6, 0.5)]
    [InlineData(0.5, 0.5)]
    [InlineData(-0.1, 0.5)]
    [InlineData(0.5, 1.1)]
    [InlineData(double.NaN, 0.5)]
    [InlineData(0.1, double.PositiveInfinity)]
    public void A_bad_band_has_no_crop(double? top, double? bottom)
    {
        Assert.Null(BalanceSuggestionEvidence.CropRect(600, 1000, top, bottom));
        Assert.Null(BalanceSuggestionEvidence.Crop(BalanceSuggestionFixtures.Image(1, 1, RenderedImage.FullHalf), top, bottom));
    }

    [Fact]
    public void The_crop_is_a_jpeg_of_the_band_rows_under_200_kb()
    {
        // The black bar sits on rows 400..440 of a 600×1000 page; the band reported around it.
        var image = BalanceSuggestionFixtures.Image(1, 1, RenderedImage.FullHalf);

        var evidence = BalanceSuggestionEvidence.Crop(image, 0.40, 0.44)!;

        var bytes = Convert.FromBase64String(evidence.Data);
        Assert.True(bytes.Length <= BalanceSuggestionEvidence.MaxBytes);
        using var crop = SKBitmap.Decode(bytes);
        Assert.Equal(600, crop.Width);
        Assert.Equal(180, crop.Height);   // rows 330..510: the band ±7%
        // The bar is in the middle of the crop (rows 70..110), white above and below.
        Assert.True(crop.GetPixel(300, 90).Red < 60);
        Assert.True(crop.GetPixel(300, 10).Red > 200);
        Assert.True(crop.GetPixel(300, 170).Red > 200);
    }

    [Fact]
    public void A_large_noisy_crop_is_squeezed_under_the_cap()
    {
        using var bitmap = new SKBitmap(3000, 3000);
        var random = new Random(7);
        for (var y = 0; y < bitmap.Height; y += 2)
            for (var x = 0; x < bitmap.Width; x += 2)
                bitmap.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        using var image = SKImage.FromBitmap(bitmap);
        using var data  = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        var rendered = new RenderedImage(1, 1, RenderedImage.FullHalf, 3000, 3000, Convert.ToBase64String(data.ToArray()));

        var evidence = BalanceSuggestionEvidence.Crop(rendered, 0.10, 0.60);

        Assert.NotNull(evidence);
        Assert.True(Convert.FromBase64String(evidence!.Data).Length <= BalanceSuggestionEvidence.MaxBytes);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static string WithNode(string json, string key, JsonNode? value)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        root[key] = value;
        return root.ToJsonString();
    }

    private static string Without(string json, string key)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        root.Remove(key);
        return root.ToJsonString();
    }
}
