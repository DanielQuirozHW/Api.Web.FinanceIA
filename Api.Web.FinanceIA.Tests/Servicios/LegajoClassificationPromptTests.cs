// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-28

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Legajo;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Pins the classification prompt and tool schema to <see cref="LegajoClassificationPrompt.PromptVersion"/>,
/// in the ExtractionPromptTests pattern: any edit to either text needs a version bump and a new
/// fingerprint here. Also checks the schema is valid for strict tool use.
/// </summary>
public sealed class LegajoClassificationPromptTests
{
    [Fact]
    public void Prompt_Fingerprint_MatchesItsVersion()
    {
        var fingerprints = new Dictionary<string, string>
        {
            // 1.0 — M10 Phase 2a: first classification prompt (type, period, holder CUIT, engagement,
            // composite). Synthetic wording only; no person is named anywhere in it.
            ["1.0"] = "b168b26c0a32989414752be9ab55fb71538c78230c9b92772a82d0fc423b64f7",
        };

        var version = LegajoClassificationPrompt.PromptVersion;
        var actual  = Fingerprint();

        Assert.True(
            fingerprints.TryGetValue(version, out var expected),
            $"No fingerprint registered for LegajoClassificationPrompt.PromptVersion \"{version}\". " +
            $"Add [\"{version}\"] = \"{actual}\".");

        Assert.True(
            expected == actual,
            $"The classification prompt or schema changed but PromptVersion is still \"{version}\". " +
            $"Bump it and register [\"<new version>\"] = \"{actual}\".");
    }

    [Fact]
    public void ToolSchema_IsValidForStrictToolUse()
    {
        using var schema = JsonDocument.Parse(LegajoClassificationPrompt.ToolSchemaJson);

        AssertStrictObject(schema.RootElement);
    }

    [Fact]
    public void ToolSchema_DocumentTypeEnum_IsTheDetectableTypes()
    {
        using var schema = JsonDocument.Parse(LegajoClassificationPrompt.ToolSchemaJson);
        var properties = schema.RootElement.GetProperty("properties");

        var top = properties.GetProperty("document_type").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()!).ToList();
        var segment = properties.GetProperty("segments").GetProperty("items").GetProperty("properties")
            .GetProperty("document_type").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToList();

        Assert.Equal(LegajoClassificationPrompt.DetectableTypes, top);
        Assert.Equal(LegajoClassificationPrompt.DetectableTypes, segment);
        Assert.Equal(DocumentTypes.LegajoTypes.Count + 1, top.Count);
    }

    [Fact]
    public void ToolSchema_EngagementEnum_IsEngagementTypesPlusDesconocido()
    {
        using var schema = JsonDocument.Parse(LegajoClassificationPrompt.ToolSchemaJson);

        var values = schema.RootElement.GetProperty("properties").GetProperty("engagement_type").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()!).ToList();

        Assert.Equal([.. EngagementTypes.Known, LegajoClassificationPrompt.UnknownType], values);
    }

    [Fact]
    public void UserText_CarriesPageCountsOnly()
    {
        Assert.Equal("Classify this document.", LegajoClassificationPrompt.BuildUserText(3, 3));
        Assert.Contains("only the first 30", LegajoClassificationPrompt.BuildUserText(40, 30));
    }

    private static string Fingerprint()
    {
        var text = LegajoClassificationPrompt.SystemPrompt + "\n" + LegajoClassificationPrompt.ToolSchemaJson
                 + "\n" + LegajoClassificationPrompt.ToolDescription;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>Strict tool use: every object closes additionalProperties and requires every property; no numeric or length constraints.</summary>
    private static void AssertStrictObject(JsonElement element)
    {
        foreach (var forbidden in new[] { "minimum", "maximum", "minLength", "maxLength", "pattern", "multipleOf" })
            Assert.False(element.TryGetProperty(forbidden, out _), $"strict schemas reject '{forbidden}'");

        if (element.TryGetProperty("properties", out var properties))
        {
            Assert.False(element.GetProperty("additionalProperties").GetBoolean());

            var required = element.GetProperty("required").EnumerateArray().Select(r => r.GetString()!).ToHashSet();
            var names = properties.EnumerateObject().Select(p => p.Name).ToHashSet();
            Assert.Equal(names, required);

            foreach (var property in properties.EnumerateObject())
                AssertStrictObject(property.Value);
        }

        if (element.TryGetProperty("items", out var items))
            AssertStrictObject(items);
    }
}
