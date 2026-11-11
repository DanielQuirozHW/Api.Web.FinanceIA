// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-01

using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The three additions Phase 3a makes to the shared extraction machinery, each on a small synthetic schema:
/// the Period value kind (generated schema, reader, correction message), the schema-level requirements
/// (reported in the missing queue) and the per-schema max_tokens override.
/// </summary>
public sealed class LegajoSchemaExtensionsTests
{
    /// <summary>A minimal schema: one period field, rows with a period, a requirement on the rows.</summary>
    private static LegajoExtractionSchema PeriodSchema(int? maxTokens = null) => new()
    {
        DocumentType    = DocumentTypes.Otro,
        SchemaVersion   = "1.0",
        PromptVersion   = "test-1.0",
        ToolName        = "record_test",
        ToolDescription = "Test.",
        SystemPrompt    = "Test.",
        Root = new LegajoSchemaObject(
        [
            new("header", new LegajoSchemaObject(
            [
                new("holderCuit", new LegajoSchemaField(LegajoValueSpec.Cuit)),
                new("lastPeriod", new LegajoSchemaField(LegajoValueSpec.Period))
            ])),
            new("rows", new LegajoSchemaArray(new LegajoSchemaObject(
            [
                new("period", new LegajoSchemaValue(LegajoValueSpec.Period)),
                new("amount", new LegajoSchemaValue(LegajoValueSpec.Money))
            ], ItemProvenance: true)))
        ]),
        RequiredFieldPaths = [],
        Requirements       = [new("/rows", tree => LegajoJsonValues.ArrayAt(tree, "/rows")
                                    .Any(r => LegajoJsonValues.Period(r?["period"]) is not null))],
        MaxTokens          = maxTokens
    };

    private static LegajoExtractionReading Read(LegajoExtractionSchema schema, string json)
    {
        using var document = JsonDocument.Parse(json);
        return LegajoToolInputReader.Read(schema.Root, document.RootElement);
    }

    private static string Input(string period, string lastPeriod = "\"2025-12\"")
        => $$$"""
           {"header":{"holderCuit":{"value":null,"page":null,"quote":null,"confidence":0.9},
                      "lastPeriod":{"value":{{{lastPeriod}}},"page":1,"quote":"Periodo 12/2025","confidence":0.9}},
            "rows":[{"period":{{{period}}},"amount":10,"page":1,"quote":"03/2025 10","confidence":0.9}]}
           """;

    // ── Period ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Period_GeneratedSchema_IsANullableStringDescribedAsYearMonth()
    {
        using var schema = JsonDocument.Parse(PeriodSchema().ToolSchemaJson);
        var root = schema.RootElement;

        var rowPeriod = root.GetProperty("properties").GetProperty("rows").GetProperty("items")
            .GetProperty("properties").GetProperty("period");
        Assert.Equal("YYYY-MM", rowPeriod.GetProperty("description").GetString());
        Assert.Equal(["string", "null"], rowPeriod.GetProperty("type").EnumerateArray().Select(t => t.GetString()!));

        // As a provenance field it gets its own $defs entry.
        Assert.True(root.GetProperty("$defs").TryGetProperty("field_period", out _));
    }

    [Theory]
    [InlineData("\"2025-03\"", "2025-03")]
    [InlineData("\"1900-01\"", "1900-01")]
    [InlineData("\"2100-12\"", "2100-12")]
    [InlineData("null", null)]
    public void Period_Reader_KeepsAValidYearMonth(string json, string? expected)
    {
        var reading = Read(PeriodSchema(), Input(json));

        Assert.True(reading.IsValid, string.Join("; ", reading.Violations));
        Assert.Equal(expected, LegajoJsonValues.String(LegajoJsonPointer.Get(reading.Result, "/rows/0/period")));
        Assert.Equal("2025-12", LegajoJsonValues.FieldString(reading.Result, "/header/lastPeriod"));
    }

    [Theory]
    [InlineData("\"2025-3\"")]
    [InlineData("\"03/2025\"")]
    [InlineData("\"2025-13\"")]
    [InlineData("\"2025-03-01\"")]
    [InlineData("\"1899-12\"")]
    [InlineData("\"2101-01\"")]
    [InlineData("202503")]
    [InlineData("\"\"")]
    public void Period_Reader_RejectsAnythingElse(string json)
    {
        var reading = Read(PeriodSchema(), Input(json));

        Assert.False(reading.IsValid);
        Assert.Contains("/rows/0/period: expected_period", reading.Violations);
    }

    [Fact]
    public void Period_JsonValues_ParseIntoTheFirstDayOfTheMonth()
    {
        Assert.Equal(new DateOnly(2025, 3, 1), LegajoJsonValues.ParsePeriod("2025-03"));
        Assert.Null(LegajoJsonValues.ParsePeriod("2025-03-31"));
        Assert.Null(LegajoJsonValues.ParsePeriod(null));
        Assert.Equal(new DateOnly(2025, 12, 1), LegajoJsonValues.Period(JsonValue.Create("2025-12")));
        Assert.Null(LegajoJsonValues.Period(JsonValue.Create(202512)));
    }

    [Fact]
    public void Period_Correction_IsNormalized_OrRejectedWithItsOwnMessage()
    {
        var schema    = PeriodSchema();
        var effective = Read(schema, Input("\"2025-03\"")).Result;

        using (var good = JsonDocument.Parse("\"2025-04\""))
        {
            var result = LegajoCorrectionValidator.Validate(schema, effective, "/rows/0/period", good.RootElement.Clone());
            Assert.True(result.IsValid, result.Error);
            Assert.Equal("2025-04", LegajoJsonValues.String(result.Value));
            Assert.Equal("2025-03", LegajoJsonValues.String(result.Original));
        }

        using var bad = JsonDocument.Parse("\"abril 2025\"");
        var rejected = LegajoCorrectionValidator.Validate(schema, effective, "/header/lastPeriod", bad.RootElement.Clone());
        Assert.Equal("/header/lastPeriod debe ser un período AAAA-MM o null.", rejected.Error);
        Assert.DoesNotContain("abril", rejected.Error);
    }

    // ── Requirements ────────────────────────────────────────────────────────────

    [Fact]
    public void Requirement_Unmet_IsReportedMissingUnderItsPath_Met_IsNot()
    {
        var schema = PeriodSchema();

        var met = Assess(schema, Read(schema, Input("\"2025-03\"")).Result);
        Assert.Empty(met.Missing);
        Assert.Equal(LegajoStatuses.Ready, met.Status);

        var unmet = Assess(schema, Read(schema, Input("null")).Result);
        Assert.Equal(["/rows"], unmet.Missing);
        Assert.Equal(LegajoStatuses.NeedsReview, unmet.Status);
    }

    [Fact]
    public void Requirement_PathAlreadyMissing_IsListedOnce()
    {
        var schema = new LegajoExtractionSchema
        {
            DocumentType       = DocumentTypes.Otro,
            SchemaVersion      = "1.0",
            PromptVersion      = "test-1.0",
            ToolName           = "record_test",
            ToolDescription    = "Test.",
            SystemPrompt       = "Test.",
            Root               = PeriodSchema().Root,
            RequiredFieldPaths = ["/header/lastPeriod"],
            Requirements       = [new("/header/lastPeriod", _ => false)]
        };

        var assessment = Assess(schema, Read(schema, Input("\"2025-03\"", lastPeriod: "null")).Result);

        Assert.Equal(["/header/lastPeriod"], assessment.Missing);
    }

    private static LegajoExtractionAssessment Assess(LegajoExtractionSchema schema, JsonObject tree)
        => LegajoExtractionAssessor.Assess(schema, tree, new LegajoIntegrityService(), null, LegajoTestData.LegajoCuit, 0.7m);

    // ── max_tokens ──────────────────────────────────────────────────────────────

    [Fact]
    public void MaxTokens_SchemaOverridesTheOption_NeverBelow1024()
    {
        var options = new LegajoOptions { ExtractionMaxTokens = 16000 };

        Assert.Equal(16000, LegajoExtractionService.MaxTokensFor(PeriodSchema(), options));
        Assert.Equal(24000, LegajoExtractionService.MaxTokensFor(PeriodSchema(maxTokens: 24000), options));
        Assert.Equal(8000, LegajoExtractionService.MaxTokensFor(PeriodSchema(maxTokens: 8000), options));
        Assert.Equal(1024, LegajoExtractionService.MaxTokensFor(PeriodSchema(maxTokens: 10), options));
        Assert.Equal(1024, LegajoExtractionService.MaxTokensFor(PeriodSchema(), new LegajoOptions { ExtractionMaxTokens = 0 }));
    }

    [Fact]
    public void MaxTokens_IsNotPartOfThePromptOrSchemaText()
    {
        Assert.Equal(PeriodSchema().ToolSchemaJson, PeriodSchema(maxTokens: 24000).ToolSchemaJson);
        Assert.DoesNotContain("24000", PeriodSchema(maxTokens: 24000).ToolSchemaJson);
    }
}
