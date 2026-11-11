// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-10

using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers <see cref="AnalysisPipeline.ResolveAlertTitle"/>: the alert Title resolution that
/// prefers the es-AR title the model now emits (extraction prompt v2.7+) and falls back to the
/// humanised alert code for analyses persisted before titles existed, or when the model
/// omits/blanks it. Reached directly as an internal static helper via the assembly's
/// InternalsVisibleTo — no controller construction needed.
/// </summary>
public sealed class AnalysisControllerAlertTitleTests
{
    // ── Title present → used (and trimmed) ──────────────────────────────────────

    [Fact]
    public void Uses_model_title_when_present()
    {
        var result = AnalysisPipeline.ResolveAlertTitle("Caída de ingresos", "REVENUE_DECLINE");

        Assert.Equal("Caída de ingresos", result);
    }

    [Fact]
    public void Trims_surrounding_whitespace_from_model_title()
    {
        var result = AnalysisPipeline.ResolveAlertTitle("  Deuda bancaria elevada \n", "HIGH_BANK_DEBT");

        Assert.Equal("Deuda bancaria elevada", result);
    }

    // ── Title absent → HumanizeCode fallback ────────────────────────────────────

    [Fact]
    public void Falls_back_to_humanized_code_when_title_null()
    {
        var result = AnalysisPipeline.ResolveAlertTitle(null, "REVENUE_DECLINE");

        // HumanizeCode title-cases every underscore-delimited token.
        Assert.Equal("Revenue Decline", result);
    }

    // ── Title empty / whitespace → HumanizeCode fallback ────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Falls_back_to_humanized_code_when_title_blank(string blankTitle)
    {
        var result = AnalysisPipeline.ResolveAlertTitle(blankTitle, "HIGH_LEVERAGE");

        Assert.Equal("High Leverage", result);
    }

    // ── Guard: nothing to work with → empty string (never throws, never null) ───

    [Fact]
    public void Returns_empty_when_both_title_and_code_absent()
    {
        var result = AnalysisPipeline.ResolveAlertTitle(null, null);

        Assert.Equal(string.Empty, result);
    }

    // ── Guard: over-long title truncated to the nvarchar(300) column width ───────

    [Fact]
    public void Truncates_over_long_title_to_column_width()
    {
        var longTitle = new string('a', 400);

        var result = AnalysisPipeline.ResolveAlertTitle(longTitle, "SOME_CODE");

        Assert.Equal(300, result.Length);
    }
}
