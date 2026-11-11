// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-28

using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// LegajoExtractionFieldResponse.Correctable against <see cref="LegajoCorrectionValidator"/>: for every
/// field the view emits, the flag is exactly what the validator decides for a value valid for that
/// field's kind. The one deliberate difference, pinned here: a row's null match-key is a whole object,
/// which the validator accepts but the view never offers.
/// </summary>
public sealed class LegajoCorrectableTests
{
    public static TheoryData<string> Scenarios => new() { "bp", "bp-null-matchkey", "mb", "mb-no-income-statement" };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Correctable_MatchesTheValidator_ForEveryEmittedField(string scenario)
    {
        var (extraction, view) = Build(scenario);
        Assert.NotEmpty(view.Fields);

        foreach (var field in view.Fields)
        {
            var node = LegajoSchemaResolver.Resolve(extraction.Schema.Root, field.FieldPath, out _);
            Assert.NotNull(node);

            if (node is LegajoSchemaObject)
            {
                // A row's null match-key: the validator takes it whole; the view does not offer it.
                Assert.False(field.Correctable, field.FieldPath);
                Assert.True(Validate(extraction, field.FieldPath, "null").IsValid, field.FieldPath);
                continue;
            }

            var result = Validate(extraction, field.FieldPath, Probe(node));
            Assert.True(result.IsValid == field.Correctable,
                $"{scenario} {field.FieldPath}: correctable {field.Correctable}, validator {(result.IsValid ? "accepts" : result.Error)}");
        }
    }

    [Fact]
    public void Manifestacion_WithoutIncomeStatement_ItsFieldsAreNotCorrectable()
    {
        var (_, without) = Build("mb-no-income-statement");
        var (_, with)    = Build("mb");

        Assert.False(Assert.Single(without.Fields, f => f.FieldPath == "/incomeStatement/ingresos").Correctable);
        Assert.True(Assert.Single(with.Fields, f => f.FieldPath == "/incomeStatement/ingresos").Correctable);
        Assert.True(Assert.Single(without.Fields, f => f.FieldPath == "/totals/totalActivo").Correctable);
    }

    [Fact]
    public void NullMatchKey_IsEmittedWhole_NotCorrectable_ItsSiblingsAre()
    {
        var (_, view) = Build("bp-null-matchkey");

        Assert.False(Assert.Single(view.Fields, f => f.FieldPath == "/items/2/matchKey").Correctable);
        Assert.True(Assert.Single(view.Fields, f => f.FieldPath == "/items/2/ownershipPct").Correctable);
        Assert.True(Assert.Single(view.Fields, f => f.FieldPath == "/items/0/matchKey/value").Correctable);
    }

    private static (LegajoEffectiveExtraction Extraction, LegajoExtractionViewResponse View) Build(string scenario)
    {
        var (schema, input, type) = scenario switch
        {
            "bp" => (LegajoExtractionPrompts.BienesPersonales, LegajoExtractionFixtures.BienesPersonales(), DocumentTypes.BienesPersonales),
            "bp-null-matchkey" => (LegajoExtractionPrompts.BienesPersonales, WithNull(LegajoExtractionFixtures.BienesPersonales(), "/items/2/matchKey"),
                                   DocumentTypes.BienesPersonales),
            "mb" => (LegajoExtractionPrompts.ManifestacionBienes, LegajoExtractionFixtures.ManifestacionBienes(), DocumentTypes.ManifestacionBienes),
            "mb-no-income-statement" => (LegajoExtractionPrompts.ManifestacionBienes,
                                         WithNull(LegajoExtractionFixtures.ManifestacionBienes(), "/incomeStatement"),
                                         DocumentTypes.ManifestacionBienes),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        var run = new LegajoExtraction
        {
            ExtractionId  = Guid.NewGuid(),
            TenantId      = LegajoTestData.Tenant,
            DocumentId    = Guid.NewGuid(),
            Kind          = LegajoExtractionKinds.Extraction,
            DocumentType  = type,
            SchemaVersion = "1.0",
            ReadMode      = LegajoReadModes.Document,
            Status        = LegajoExtractionStatuses.Succeeded,
            ResultJson    = LegajoExtractionFixtures.Normalize(schema, input).ToJsonString(),
            IsCurrent     = true
        };

        var extraction = LegajoExtractionReview.Evaluate(
            run, [], new LegajoIntegrityService(), new DateOnly(2025, 12, 31), LegajoTestData.LegajoCuit, 0.6m);
        Assert.NotNull(extraction);

        var document = new Document { DocumentId = run.DocumentId, TenantId = run.TenantId, LegajoStatus = LegajoStatuses.NeedsReview };
        return (extraction, LegajoExtractionReview.BuildView(extraction, document, new Dictionary<Guid, string>()));
    }

    private static JsonObject WithNull(JsonObject input, string pointer)
    {
        Assert.True(LegajoJsonPointer.Set(input, pointer, null));
        return input;
    }

    private static LegajoCorrectionValidator.Result Validate(LegajoEffectiveExtraction extraction, string path, string json)
    {
        using var value = JsonDocument.Parse(json);
        return LegajoCorrectionValidator.Validate(extraction.Schema, extraction.Effective, path, value.RootElement.Clone());
    }

    // A value valid for the leaf's kind, so only the structural rule can reject it.
    private static string Probe(LegajoSchemaNode node)
    {
        var spec = node switch
        {
            LegajoSchemaField f => f.Spec,
            LegajoSchemaValue v => v.Spec,
            _ => throw new ArgumentOutOfRangeException(nameof(node))
        };

        return spec.Kind switch
        {
            LegajoValueKind.Money or LegajoValueKind.Integer => "1",
            LegajoValueKind.Percent => "50",
            LegajoValueKind.Boolean => "true",
            LegajoValueKind.Date    => "\"2025-12-31\"",
            LegajoValueKind.Enum    => JsonSerializer.Serialize(spec.EnumValues![0]),
            LegajoValueKind.Cuit    => JsonSerializer.Serialize(LegajoTestData.LegajoCuitDigits),
            _                       => "\"x\""
        };
    }
}
