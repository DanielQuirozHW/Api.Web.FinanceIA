// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-06

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The Bienes Personales totalBienes requirement: met when totalBienes is printed, or when both bienesPais
/// and bienesExterior are (a printed 0 counts); reported missing under /totals/totalBienes otherwise.
/// Synthetic fixture figures only.
/// </summary>
public sealed class LegajoBienesPersonalesRequirementTests
{
    private const string TotalPath = "/totals/totalBienes";

    private static readonly LegajoIntegrityService Integrity = new();

    private static JsonObject Bp(Action<JsonObject> mutate)
        => LegajoExtractionFixtures.NormalizedClean(DocumentTypes.BienesPersonales, mutate);

    private static LegajoExtractionAssessment Assess(JsonObject tree)
        => LegajoExtractionAssessor.Assess(LegajoExtractionPrompts.BienesPersonales, tree, Integrity,
            new DateOnly(2025, 12, 31), LegajoTestData.LegajoCuit, 0.7m);

    private static bool IsMet(JsonObject tree)
        => Assert.Single(LegajoExtractionPrompts.BienesPersonales.Requirements, r => r.Path == TotalPath).IsMet(tree);

    private static void Null(JsonObject input, params string[] totals)
    {
        foreach (var name in totals)
            Assert.True(LegajoJsonPointer.Set(input, "/totals/" + name + "/value", null));
    }

    [Fact]
    public void TotalBienes_IsARequirement_NotARequiredField_AndTheOthersStayRequired()
    {
        var schema = LegajoExtractionPrompts.BienesPersonales;

        Assert.Equal(["/header/fiscalYear", "/header/amountsUnit"], schema.RequiredFieldPaths);
        Assert.Equal(["/items", "/rubroTotals"], schema.RequiredAnyArrayPaths);
        Assert.Equal([TotalPath], schema.Requirements.Select(r => r.Path));
        Assert.Equal("bp-1.1", schema.PromptVersion);
    }

    [Fact]
    public void Met_WhenTotalBienesIsPrinted_EvenWithoutItsParts()
    {
        var tree = Bp(i => Null(i, "bienesPais", "bienesExterior"));

        Assert.True(IsMet(tree));
        Assert.DoesNotContain(TotalPath, Assess(tree).Missing);
    }

    [Fact]
    public void Met_WhenPaisAndExteriorArePrinted_AndTheTotalIsNot()
    {
        var tree = Bp(i => Null(i, "totalBienes"));

        Assert.True(IsMet(tree));
        var assessment = Assess(tree);
        Assert.Empty(assessment.Missing);

        // bp_total_bienes is skipped (a null operand), never failed: the document is Ready.
        Assert.Contains(assessment.Checks.Skipped, s => s.Name == LegajoIntegrityService.BpTotalBienesCheck);
        Assert.Equal(LegajoStatuses.Ready, assessment.Status);
    }

    [Fact]
    public void Met_WhenAPrintedPartIsZero()
    {
        var tree = Bp(i =>
        {
            Null(i, "totalBienes");
            Assert.True(LegajoJsonPointer.Set(i, "/totals/bienesExterior/value", 0m));
        });

        Assert.True(IsMet(tree));
        Assert.DoesNotContain(TotalPath, Assess(tree).Missing);
    }

    [Fact]
    public void NotMet_WithOnlyPais()
    {
        var tree = Bp(i => Null(i, "totalBienes", "bienesExterior"));

        Assert.False(IsMet(tree));
        var assessment = Assess(tree);
        Assert.Equal([TotalPath], assessment.Missing);
        Assert.Equal(LegajoStatuses.NeedsReview, assessment.Status);
    }

    [Fact]
    public void NotMet_WithNoTotals()
    {
        var tree = Bp(i => Null(i, "totalBienes", "bienesPais", "bienesExterior"));

        Assert.False(IsMet(tree));
        Assert.Equal([TotalPath], Assess(tree).Missing);
    }
}
