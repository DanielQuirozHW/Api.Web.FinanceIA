// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-31

using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using Xunit;

namespace Api.Web.FinanceIA.Tests.PersonAnalysis;

/// <summary>
/// <see cref="EngineVersion"/>: persona engine versions order as dotted numbers, so "0.10" comes after "0.9" —
/// which text order gets wrong — and the analysis PromptVersion ("pf-0.10") orders the same way.
/// </summary>
public sealed class EngineVersionTests
{
    [Theory]
    [InlineData("0.10", "0.9")]
    [InlineData("0.9", "0.8")]
    [InlineData("0.11", "0.10")]
    [InlineData("1.0", "0.10")]
    [InlineData("1.0", "0.99")]
    [InlineData("pf-0.10", "pf-0.9")]
    [InlineData("0.9", "")]
    [InlineData("0.9", null)]
    public void Later_SortsAfter_Earlier(string? later, string? earlier)
    {
        Assert.True(EngineVersion.CompareVersions(later, earlier) > 0);
        Assert.True(EngineVersion.CompareVersions(earlier, later) < 0);
    }

    [Theory]
    [InlineData("0.10", "0.10")]
    [InlineData("1", "1.0")]
    [InlineData(null, "")]
    public void Equal_VersionsCompareEqual(string? x, string? y)
        => Assert.Equal(0, EngineVersion.CompareVersions(x, y));

    [Fact]
    public void Sorting_IsNumeric_WithMalformedValuesLast()
    {
        var sorted = new[] { "0.10", "0.6", "0.9", "x.y", "0.7", "0.8" }.Order(EngineVersion.Comparer).ToList();

        Assert.Equal(["0.6", "0.7", "0.8", "0.9", "0.10", "x.y"], sorted);
    }

    [Fact]
    public void TheCurrentEngine_IsTheLatest()
    {
        Assert.Equal("0.10", PersonEvaluationService.Version);
        Assert.Equal("pf-0.10", PersonAnalysesController.PromptVersion);
        foreach (var earlier in new[] { "0.5", "0.6", "0.7", "0.8", "0.9" })
        {
            Assert.True(EngineVersion.CompareVersions(PersonEvaluationService.Version, earlier) > 0);
            Assert.True(EngineVersion.CompareVersions(PersonAnalysesController.PromptVersion, "pf-" + earlier) > 0);
        }
    }
}
