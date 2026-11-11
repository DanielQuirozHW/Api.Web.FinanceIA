// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-28

using System.Reflection;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models.Financia;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Models;

/// <summary>
/// The static catalogues M10 Phase 2a added: the per-model price table and the LUFE type mapping that moved onto
/// <see cref="DocumentTypes"/>. The expected-documents defaults are pinned in LegajoExpectedDocumentsDefaultsTests.
/// </summary>
public sealed class LegajoCatalogueTests
{
    // ── Pricing ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ClaudeModelPricing_AgreesWithTheChatCatalogue_OnEverySharedModel()
    {
        foreach (var chatModel in ChatModelCatalog.All)
        {
            Assert.True(ClaudeModelPricing.PerMillionTokens.TryGetValue(chatModel.Id, out var price),
                $"{chatModel.Id} has no entry in ClaudeModelPricing.");
            Assert.Equal(chatModel.InputCostPerMTokUsd, price.Input);
            Assert.Equal(chatModel.OutputCostPerMTokUsd, price.Output);
        }
    }

    [Fact]
    public void ClaudeModelPricing_PricesEveryExtractionModel()
    {
        // AnalysisPipeline prices the extraction by ModelUsed; an unpriced catalogue entry would log a
        // null cost for every analysis that model ran.
        foreach (var spec in ExtractionModelCatalog.All)
        {
            Assert.True(ClaudeModelPricing.PerMillionTokens.ContainsKey(spec.Id),
                $"{spec.Id} has no entry in ClaudeModelPricing.");
        }
    }

    [Fact]
    public void EstimateCostUsd_Sonnet55()
    {
        // 10,000 in × $2/M + 2,000 out × $10/M = 0.02 + 0.02. Thinking tokens are inside output_tokens.
        Assert.Equal(0.04m, ClaudeModelPricing.EstimateCostUsd("claude-sonnet-5-5", 10_000, 2_000));
    }

    [Fact]
    public void EstimateCostUsd_Sonnet46_MatchesTheFormerInlineExtractionRate()
    {
        // AnalysisPipeline used (in × 3 + out × 15) / 1e6 inline before it priced by model.
        const int input = 123_457, output = 6_789;
        Assert.Equal((input * 3m + output * 15m) / 1_000_000m,
            ClaudeModelPricing.EstimateCostUsd("claude-sonnet-4-6", input, output));
    }

    [Fact]
    public void EstimateCostUsd_Haiku45()
    {
        // 4,200 in × $1/M + 310 out × $5/M = 0.0042 + 0.00155
        Assert.Equal(0.00575m, ClaudeModelPricing.EstimateCostUsd("claude-haiku-4-5", 4200, 310));
    }

    [Fact]
    public void EstimateCostUsd_UnknownModel_IsNull_NotZero()
    {
        Assert.Null(ClaudeModelPricing.EstimateCostUsd("claude-unknown", 1000, 1000));
        Assert.Null(ClaudeModelPricing.EstimateCostUsd(null, 1000, 1000));
    }

    // ── LUFE mapping keeps its output ───────────────────────────────────────────

    [Theory]
    [InlineData("Balance 2024", "Balance")]
    [InlineData("BALANCE general", "Balance")]
    [InlineData("Certificado MiPyME", "Certificado")]
    [InlineData("Estatuto", "Otro")]
    [InlineData(null, "Otro")]
    public void MapLufeDocumentType_KeepsItsOutput(string? nombre, string expected)
    {
        var method = typeof(EntitiesController).GetMethod("MapLufeDocumentType", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.Equal(expected, method.Invoke(null, [nombre]));
    }
}
