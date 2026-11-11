// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-28

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Pins each extraction prompt (Phase 2b and 3a) + generated schema + tool description to its PromptVersion (the
/// ExtractionPromptTests pattern): any edit needs a version bump and a new fingerprint here. Also keeps
/// the schemas strict-compatible (so strict mode stays a one-line switch) and the default model tied to
/// balance extraction's.
/// </summary>
public sealed class LegajoExtractionPromptTests
{
    /// <summary>Every type with a schema: Phase 2b (BP, MB) and Phase 3a.</summary>
    public static TheoryData<string> Types => [.. LegajoExtractionFixtures.Schemas.Keys];

    /// <summary>The Phase 3a types.</summary>
    public static TheoryData<string> TaxTypes =>
    [
        DocumentTypes.Ganancias, DocumentTypes.DdjjIva, DocumentTypes.DdjjIibb, DocumentTypes.CertificacionIngresos,
        DocumentTypes.Monotributo
    ];

    private static LegajoExtractionSchema SchemaOf(string documentType) => LegajoExtractionFixtures.Schemas[documentType];

    private static readonly Dictionary<string, string> PromptPrefixes = new()
    {
        [DocumentTypes.BienesPersonales]      = "bp-",
        [DocumentTypes.ManifestacionBienes]   = "mb-",
        [DocumentTypes.Ganancias]             = "gan-",
        [DocumentTypes.DdjjIva]               = "iva-",
        [DocumentTypes.DdjjIibb]              = "iibb-",
        [DocumentTypes.CertificacionIngresos] = "ci-",
        [DocumentTypes.Monotributo]           = "mono-"
    };

    private static readonly Dictionary<string, string> Fingerprints = new()
    {
        // bp-1.0 — M10 Phase 2b: first Bienes Personales (F.762) extraction. Synthetic wording only.
        ["bp-1.0"] = "a3cc48fcd21813afabf181fabd72ae308d052838d90f5ac768417d367be7bafc",
        // mb-1.0 — M10 Phase 2b: first manifestación de bienes extraction.
        ["mb-1.0"] = "6584108aa3a023e9362506279d4065e5972fe3182f8c3510b45974747c52a38b",
        // mb-1.1 — review of #239: an unstated unit is null (the document goes to review with amountsUnit
        // missing) instead of an assumed Pesos. bp-1.0 is unchanged: an F.762 is always in pesos.
        ["mb-1.1"] = "a32eb64b989a860d928783fcff12e22fb5a801fb070077fa2370fc485520475b",
        // mb-1.2 — engine 0.8: contingencies.guaranteesGiven, the avales y/o coobligaciones otorgados (null when
        // the document prints none), exposed on the inventory read and never applied to the evaluation.
        ["mb-1.2"] = "363ef8e52843a395f5335b3afe346cdaf77b06c60aaf9b37a04e3d7a4eb1b894",
        // mb-1.3 — engine 0.9: annexItems gain a nullable investmentClass (TituloPublico, AccionCotizante,
        // ObligacionNegociable, CuotaparteFci, Otra; only on an Inversion item, never guessed), and the kind rule says a
        // listed share (exchange, ticker or market price printed) is Inversion even with the issuer's CUIT.
        ["mb-1.3"] = "9b999d3931759f776782e98eca0213b6161221c65a5b536c5366e89274b211f5",
        // bp-1.1 — engine 0.9: TitulosYAcciones items gain the same nullable investmentClass (null for unlisted shares
        // and participaciones sociales). The shared reading rules are byte-identical to bp-1.0's.
        ["bp-1.1"] = "936968b55bd133c4e978c980d23006934bd17a62d00c2160818dbe0e02a97c70",
        // gan-1.0 — M10 Phase 3a: first Ganancias (F.711) extraction.
        ["gan-1.0"] = "c0448c28023097af0a84d5779f9d33c42cde88a85e602bc4ce8f83e39222c578",
        // iva-1.0 — M10 Phase 3a: first DDJJ IVA (F.2002 / F.731) extraction.
        ["iva-1.0"] = "f4b734081daca429f0c9aa0c46d8b1a7d984085514509f53f7f6fc87b23511a6",
        // iibb-1.0 — M10 Phase 3a: first DDJJ Ingresos Brutos (CM03 / CM05 / local) extraction.
        ["iibb-1.0"] = "dbbd1cf685040f626bea1e725e66a6dbe615db3ddd056eff598626d06aa8bb4e",
        // ci-1.0 — M10 Phase 3a: first certificación de ingresos extraction.
        ["ci-1.0"] = "e01c199945cf70d27662de827a6784d59e74309975696c10d96a1ebc64d78797",
        // mono-1.0 — M10 Phase 3a: first monotributo extraction.
        ["mono-1.0"] = "fab542d527bb2f7f04f97a50df5afc2f6f459e8ca39e52a3cb544c5f632746bb",
    };

    [Theory]
    [MemberData(nameof(Types))]
    public void Prompt_Fingerprint_MatchesItsVersion(string documentType)
    {
        var schema  = SchemaOf(documentType);
        var actual  = Fingerprint(schema);
        var version = schema.PromptVersion;

        Assert.True(
            Fingerprints.TryGetValue(version, out var expected),
            $"No fingerprint registered for PromptVersion \"{version}\". Add [\"{version}\"] = \"{actual}\".");

        Assert.True(
            expected == actual,
            $"The {documentType} prompt or schema changed but PromptVersion is still \"{version}\". " +
            $"Bump it and register [\"<new version>\"] = \"{actual}\".");
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void ToolSchema_IsStrictCompatible(string documentType)
    {
        using var schema = JsonDocument.Parse(SchemaOf(documentType).ToolSchemaJson);

        AssertStrictObject(schema.RootElement);
        foreach (var definition in schema.RootElement.GetProperty("$defs").EnumerateObject())
            AssertStrictObject(definition.Value);
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void Schema_Versions_And_RequiredPaths_Resolve(string documentType)
    {
        var schema = SchemaOf(documentType);

        Assert.Equal("1.0", schema.SchemaVersion);
        Assert.Equal(documentType, schema.DocumentType);
        Assert.StartsWith(PromptPrefixes[documentType], schema.PromptVersion);

        var normalized = LegajoExtractionFixtures.NormalizedClean(documentType);

        foreach (var path in schema.RequiredFieldPaths.Concat(schema.RequiredAnyArrayPaths).Concat(schema.Requirements.Select(r => r.Path)))
        {
            LegajoJsonPointer.Get(normalized, path, out var found);
            Assert.True(found, path);
            Assert.NotNull(LegajoSchemaResolver.Resolve(schema.Root, path, out _));
        }

        // The clean fixture meets every requirement.
        Assert.All(schema.Requirements, r => Assert.True(r.IsMet(normalized), r.Path));

        LegajoJsonPointer.Get(normalized, LegajoExtractionVocabulary.HolderCuitPath, out var cuitFound);
        Assert.True(cuitFound);
    }

    [Fact]
    public void ToolNames_AndPromptVersions_AreUnique()
    {
        var schemas = LegajoExtractionFixtures.Schemas.Values.ToList();

        Assert.Equal(schemas.Count, schemas.Select(s => s.ToolName).Distinct().Count());
        Assert.Equal(schemas.Count, schemas.Select(s => s.PromptVersion).Distinct().Count());
        Assert.All(schemas, s => Assert.True(s.PromptVersion.Length <= 20, s.PromptVersion));   // nvarchar(20)
    }

    [Fact]
    public void SupportedTypes_AreExactlyTheSevenWithASchema()
    {
        Assert.Equal(
        [
            DocumentTypes.BienesPersonales, DocumentTypes.ManifestacionBienes, DocumentTypes.Ganancias, DocumentTypes.DdjjIva,
            DocumentTypes.DdjjIibb, DocumentTypes.CertificacionIngresos, DocumentTypes.Monotributo
        ], LegajoExtractionPrompts.SupportedTypes);

        foreach (var type in DocumentTypes.LegajoTypes)
            Assert.Equal(LegajoExtractionPrompts.SupportedTypes.Contains(type), LegajoExtractionPrompts.For(type) is not null);

        foreach (var (type, schema) in LegajoExtractionFixtures.Schemas)
            Assert.Same(schema, LegajoExtractionPrompts.For(type));

        // Still waiting for an extractor: REIBP, the projected cash flow, the authorization and Otro.
        foreach (var type in new[] { DocumentTypes.Reibp, DocumentTypes.FlujoProyectado, DocumentTypes.AutorizacionDatos, DocumentTypes.Otro })
            Assert.Null(LegajoExtractionPrompts.For(type));
    }

    [Fact]
    public void DefaultModel_IsBalanceExtractionsModel()
    {
        // The balance extraction's default now lives in its catalogue, not in a ClaudeService constant.
        Assert.Equal(LegajoExtractionOptions.DefaultModel, ExtractionModelCatalog.Default.Id);
        Assert.True(ClaudeModelPricing.PerMillionTokens.ContainsKey(LegajoExtractionOptions.DefaultModel));
        Assert.True(LegajoModelSupport.SupportsForcedToolUse(LegajoExtractionOptions.DefaultModel));
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void Prompt_NamesTheRules_ThatKeepTheModelFromComputing(string documentType)
    {
        var prompt = SchemaOf(documentType).SystemPrompt;

        Assert.Contains("Read, do not compute", prompt);
        Assert.Contains("amountsUnit", prompt);
        Assert.Contains("is null: never infer it", prompt);
        if (documentType == DocumentTypes.BienesPersonales)
            Assert.Contains("FISCAL values", prompt);
    }

    [Fact]
    public void UnstatedUnit_IsPesosForBp_NullForMb()
    {
        Assert.Contains("When no unit is stated, Pesos.", LegajoExtractionPrompts.BienesPersonales.SystemPrompt);
        Assert.DoesNotContain("does not state a unit, null", LegajoExtractionPrompts.BienesPersonales.SystemPrompt);

        Assert.Contains("When the document does not state a unit, null.", LegajoExtractionPrompts.ManifestacionBienes.SystemPrompt);
        Assert.DoesNotContain("When no unit is stated, Pesos.", LegajoExtractionPrompts.ManifestacionBienes.SystemPrompt);
    }

    [Theory]
    [InlineData(DocumentTypes.Ganancias, true)]
    [InlineData(DocumentTypes.DdjjIva, true)]
    [InlineData(DocumentTypes.DdjjIibb, true)]
    [InlineData(DocumentTypes.Monotributo, true)]
    [InlineData(DocumentTypes.CertificacionIngresos, false)]
    public void UnstatedUnit_IsPesosForTheAfipForms_NullForTheCertificacion(string documentType, bool pesos)
    {
        var prompt = SchemaOf(documentType).SystemPrompt;

        Assert.Equal(pesos, prompt.Contains("When no unit is stated, Pesos.", StringComparison.Ordinal));
        Assert.Equal(!pesos, prompt.Contains("When the document does not state a unit, null.", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void AmountsUnit_IsRequired(string documentType)
        => Assert.Contains(LegajoExtractionVocabulary.AmountsUnitPath, SchemaOf(documentType).RequiredFieldPaths);

    [Theory]
    [MemberData(nameof(TaxTypes))]
    public void TaxPrompts_HaveNoAssets_NoMatchKey_AndKeepTheHolderRule(string documentType)
    {
        var schema = SchemaOf(documentType);

        Assert.DoesNotContain("matchKey", schema.SystemPrompt);
        Assert.DoesNotContain("matchKey", schema.ToolSchemaJson);
        Assert.Contains("Each field outside an array is an object {value, page, quote, confidence}", schema.SystemPrompt);
        Assert.Contains("holderCuit: the CUIT/CUIL of the person the document is ABOUT", schema.SystemPrompt);
        Assert.Contains("without the name of any person", schema.SystemPrompt);
        Assert.Equal(schema.ToolSchemaJson.Contains("\"YYYY-MM\"", StringComparison.Ordinal),
                     schema.SystemPrompt.Contains("as YYYY-MM", StringComparison.Ordinal));
    }

    [Fact]
    public void Ganancias_Prompt_ReadsTheMontoConsumido_NeverComputesIt_AndSignsQuebrantos()
    {
        var prompt = LegajoExtractionPrompts.Ganancias.SystemPrompt;

        Assert.Contains("montoConsumido: the monto consumido the document prints", prompt);
        Assert.Contains("read it, never compute it", prompt);
        Assert.Contains("labelled as a quebranto) is a negative number", prompt);
    }

    [Fact]
    public void CertificacionIngresos_Prompt_NeverTheAccountantsNameOrCuit()
        => Assert.Contains("never the accountant's name or CUIT", LegajoExtractionPrompts.CertificacionIngresos.SystemPrompt);

    [Theory]
    [InlineData(DocumentTypes.BienesPersonales, null)]
    [InlineData(DocumentTypes.ManifestacionBienes, null)]
    [InlineData(DocumentTypes.Ganancias, null)]
    [InlineData(DocumentTypes.DdjjIva, 24000)]
    [InlineData(DocumentTypes.DdjjIibb, 24000)]
    [InlineData(DocumentTypes.CertificacionIngresos, null)]
    [InlineData(DocumentTypes.Monotributo, null)]
    public void MaxTokens_PerType(string documentType, int? expected)
    {
        Assert.Equal(expected, SchemaOf(documentType).MaxTokens);
        Assert.Equal(expected ?? 16000, LegajoExtractionService.MaxTokensFor(SchemaOf(documentType), new LegajoOptions()));
    }

    [Fact]
    public void UserText_CarriesThePageCountOnly()
    {
        Assert.Equal("Extract this document. It has 1 page.", LegajoExtractionPrompts.BuildUserText(1));
        Assert.Equal("Extract this document. It has 7 pages.", LegajoExtractionPrompts.BuildUserText(7));
    }

    private static string Fingerprint(LegajoExtractionSchema schema)
    {
        var text = schema.SystemPrompt + "\n" + schema.ToolSchemaJson + "\n" + schema.ToolDescription + "\n" + schema.ToolName;
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
