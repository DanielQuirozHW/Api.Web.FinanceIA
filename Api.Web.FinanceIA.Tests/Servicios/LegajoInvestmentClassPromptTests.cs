// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-31

using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// mb-1.3 / bp-1.1: the nullable investmentClass in both schemas and in the strict tool schema, the rule text that
/// explains it (and fixes the listed-share misclassification), the reader and the correction path accepting null,
/// and the synthetic investment fixtures (a listed share with the issuer's CUIT, a bono, an FCI) passing every
/// integrity check. The live harness needs a real PDF and never runs in CI; these fixtures stand in for its readings.
/// </summary>
public sealed class LegajoInvestmentClassPromptTests
{
    private static readonly DateOnly PeriodEnd2025 = new(2025, 12, 31);

    public static TheoryData<string, string> ClassPaths => new()
    {
        { DocumentTypes.ManifestacionBienes, "/annexItems/0/investmentClass" },
        { DocumentTypes.BienesPersonales,    "/items/0/investmentClass" }
    };

    private static LegajoExtractionSchema SchemaOf(string documentType) => LegajoExtractionFixtures.Schemas[documentType];

    // ── Schema ──────────────────────────────────────────────────────────────────

    [Fact]
    public void PromptVersions_AreMb13AndBp11_OnTheSameSchemaVersion()
    {
        Assert.Equal("mb-1.3", LegajoExtractionPrompts.ManifestacionBienes.PromptVersion);
        Assert.Equal("bp-1.1", LegajoExtractionPrompts.BienesPersonales.PromptVersion);
        Assert.Equal("1.0", LegajoExtractionPrompts.ManifestacionBienes.SchemaVersion);
        Assert.Equal("1.0", LegajoExtractionPrompts.BienesPersonales.SchemaVersion);
    }

    [Theory]
    [MemberData(nameof(ClassPaths))]
    public void TheClass_IsANullableEnum_OfExactlyTheConstants(string documentType, string path)
    {
        var node = Assert.IsType<LegajoSchemaValue>(LegajoSchemaResolver.Resolve(SchemaOf(documentType).Root, path, out var insideItem));

        Assert.True(insideItem);
        Assert.True(node.NullableEnum);
        Assert.True(LegajoToolInputReader.AllowsNull(node));
        Assert.Equal(LegajoValueKind.Enum, node.Spec.Kind);
        Assert.Equal(LegajoInvestmentClasses.Known, node.Spec.EnumValues);
    }

    [Theory]
    [InlineData(DocumentTypes.ManifestacionBienes, "/annexItems/0/kind")]
    [InlineData(DocumentTypes.BienesPersonales, "/items/0/rubro")]
    public void TheOtherItemEnums_StayRequired(string documentType, string path)
    {
        var node = Assert.IsType<LegajoSchemaValue>(LegajoSchemaResolver.Resolve(SchemaOf(documentType).Root, path, out _));

        Assert.False(node.NullableEnum);
        Assert.False(LegajoToolInputReader.AllowsNull(node));
    }

    [Theory]
    [InlineData(DocumentTypes.ManifestacionBienes)]
    [InlineData(DocumentTypes.BienesPersonales)]
    public void TheToolSchema_DeclaresTheClass_AsStringOrNull_WithNullInItsEnum(string documentType)
    {
        var schema = JsonNode.Parse(SchemaOf(documentType).ToolSchemaJson)!;
        var property = Assert.Single(PropertiesNamed(schema, "investmentClass"));

        Assert.Equal(["string", "null"], property["type"]!.AsArray().Select(t => (string?)t));
        var values = property["enum"]!.AsArray().Select(v => (string?)v).ToList();
        Assert.Equal([.. LegajoInvestmentClasses.Known, null], values);
    }

    [Theory]
    [InlineData(DocumentTypes.ManifestacionBienes)]
    [InlineData(DocumentTypes.BienesPersonales)]
    public void TheReader_AcceptsAMissingOrNullClass_AndRejectsAnUnknownOne(string documentType)
    {
        var array = documentType == DocumentTypes.ManifestacionBienes ? "annexItems" : "items";
        var schema = SchemaOf(documentType);

        // Missing: the clean fixtures carry no investmentClass (an older run's shape).
        var clean = LegajoExtractionFixtures.Clean(documentType);
        Assert.True(Read(schema, clean).IsValid);

        ((JsonObject)clean[array]!.AsArray()[0]!)["investmentClass"] = null;
        Assert.True(Read(schema, clean).IsValid);

        ((JsonObject)clean[array]!.AsArray()[0]!)["investmentClass"] = "Bono";
        var reading = Read(schema, clean);
        Assert.False(reading.IsValid);
        Assert.Contains(reading.Violations, v => v.StartsWith($"/{array}/0/investmentClass", StringComparison.Ordinal));
    }

    // ── Prompt text ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DocumentTypes.ManifestacionBienes)]
    [InlineData(DocumentTypes.BienesPersonales)]
    public void ThePrompt_ExplainsEveryClass_AndNeverGuesses(string documentType)
    {
        var prompt = SchemaOf(documentType).SystemPrompt;

        Assert.All(LegajoInvestmentClasses.Known, c => Assert.Contains(c, prompt));
        Assert.Contains("bonos, letras del Tesoro, títulos de deuda soberana, provincial or municipal", prompt);
        Assert.Contains("prints a stock exchange, a ticker or a market price", prompt);
        Assert.Contains("obligaciones negociables", prompt);
        Assert.Contains("cuotapartes de fondos comunes de inversión", prompt);
        Assert.Contains("Never guess: when the document gives no hint of what the investment is, investmentClass is null.", prompt);
    }

    [Fact]
    public void TheManifestacionPrompt_FilesAListedShareAsAnInversion_EvenWithTheIssuersCuit()
    {
        var prompt = LegajoExtractionPrompts.ManifestacionBienes.SystemPrompt;

        Assert.Contains(
            "A share for which the document prints a stock exchange, a ticker or a market price (a listed share) is " +
            "Inversion, even when the issuer's CUIT is printed", prompt);
        Assert.Contains("or cuotas or participaciones sociales of a company, is Participacion", prompt);
        Assert.Contains("investmentClass: only when kind is Inversion, otherwise null.", prompt);
    }

    [Fact]
    public void TheBienesPersonalesPrompt_ClassifiesOnlyTitulosYAcciones_AndLeavesUnlistedSharesNull()
    {
        var prompt = LegajoExtractionPrompts.BienesPersonales.SystemPrompt;

        Assert.Contains("investmentClass: only for a TitulosYAcciones row, otherwise null.", prompt);
        Assert.Contains("a share without a quotation, or cuotas or participaciones sociales of a company, investmentClass is null", prompt);
    }

    // ── Correction path ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("null", true)]
    [InlineData("\"TituloPublico\"", true)]
    [InlineData("\"CuotaparteFci\"", true)]
    [InlineData("\"Otra\"", true)]
    [InlineData("\"Bono\"", false)]
    public void AnAnalystCorrection_OfTheClass_AcceptsNullOrAKnownValue(string json, bool valid)
    {
        var schema = LegajoExtractionPrompts.ManifestacionBienes;
        var effective = LegajoExtractionFixtures.Normalize(schema, LegajoExtractionFixtures.ManifestacionBienesWithInvestments());
        using var value = JsonDocument.Parse(json);

        var result = LegajoCorrectionValidator.Validate(schema, effective, "/annexItems/3/investmentClass", value.RootElement.Clone());

        Assert.Equal(valid, result.IsValid);
    }

    // ── Fixtures (the harness readings: listed share, bono, FCI) ────────────────

    [Fact]
    public void TheInvestmentFixtures_Normalize_AndPassEveryIntegrityCheck()
    {
        var integrity = new LegajoIntegrityService();

        var mb = LegajoExtractionFixtures.Normalize(LegajoExtractionPrompts.ManifestacionBienes, LegajoExtractionFixtures.ManifestacionBienesWithInvestments());
        var mbReport = integrity.Evaluate(DocumentTypes.ManifestacionBienes, mb, PeriodEnd2025);
        Assert.True(mbReport.Passed, string.Join("; ", mbReport.Failed.Select(c => c.Message)));

        var bp = LegajoExtractionFixtures.Normalize(LegajoExtractionPrompts.BienesPersonales, LegajoExtractionFixtures.BienesPersonalesWithInvestments());
        var bpReport = integrity.Evaluate(DocumentTypes.BienesPersonales, bp, PeriodEnd2025);
        Assert.True(bpReport.Passed, string.Join("; ", bpReport.Failed.Select(c => c.Message)));

        Assert.Equal(
            ["AccionCotizante", "TituloPublico", "CuotaparteFci"],
            mb["annexItems"]!.AsArray().Select(r => (string?)r!["investmentClass"]).Where(c => c is not null));
        Assert.Equal(
            ["AccionCotizante", "TituloPublico", "CuotaparteFci"],
            bp["items"]!.AsArray().Select(r => (string?)r!["investmentClass"]).Where(c => c is not null));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static LegajoExtractionReading Read(LegajoExtractionSchema schema, JsonObject input)
    {
        using var document = JsonDocument.Parse(input.ToJsonString());
        return LegajoToolInputReader.Read(schema.Root, document.RootElement);
    }

    private static IEnumerable<JsonObject> PropertiesNamed(JsonNode node, string name)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["properties"] is JsonObject props && props[name] is JsonObject found)
                    yield return found;
                foreach (var (_, child) in obj)
                    if (child is not null)
                        foreach (var hit in PropertiesNamed(child, name))
                            yield return hit;
                break;
            case JsonArray array:
                foreach (var child in array)
                    if (child is not null)
                        foreach (var hit in PropertiesNamed(child, name))
                            yield return hit;
                break;
        }
    }
}
