// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-28

using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// <see cref="LegajoEffectiveValues"/> (the run overlaid with its corrections, in order, latest wins —
/// never modifying the run) and <see cref="LegajoCorrectionValidator"/> (path and type validation).
/// </summary>
public sealed class LegajoCorrectionTests
{
    private static readonly LegajoExtractionSchema Bp = LegajoExtractionPrompts.BienesPersonales;

    private static string BpResultJson()
        => LegajoExtractionFixtures.Normalize(Bp, LegajoExtractionFixtures.BienesPersonales()).ToJsonString();

    private static LegajoCorrection Correction(string path, string? valueJson, int minutesAgo) => new()
    {
        CorrectionId       = Guid.NewGuid(),
        TenantId           = LegajoTestData.Tenant,
        ExtractionId       = Guid.NewGuid(),
        FieldPath          = path,
        CorrectedValueJson = valueJson,
        Reason             = "Leido del original",
        CorrectedBy        = Guid.NewGuid(),
        CorrectedAt        = DateTime.UtcNow.AddMinutes(-minutesAgo)
    };

    private static LegajoCorrectionValidator.Result Validate(string? path, string json, JsonObject? effective = null)
    {
        using var document = JsonDocument.Parse(json);
        return LegajoCorrectionValidator.Validate(Bp, effective ?? JsonNode.Parse(BpResultJson())!.AsObject(), path,
                                                  document.RootElement.Clone());
    }

    // ── Effective values ────────────────────────────────────────────────────────

    [Fact]
    public void Apply_InCorrectedAtOrder_LatestWins_KeepsTheModelsProvenance()
    {
        var resultJson = BpResultJson();

        var (effective, corrected) = LegajoEffectiveValues.Apply(Bp, resultJson,
        [
            Correction("/totals/totalBienes", "1800000", minutesAgo: 1),   // later
            Correction("/totals/totalBienes", "1750000", minutesAgo: 5),   // earlier
            Correction("/items/1/fiscalValue", "260000", minutesAgo: 3)
        ]);

        Assert.Equal(1_800_000m, LegajoJsonValues.FieldDecimal(effective, "/totals/totalBienes"));
        Assert.Equal(1, LegajoJsonValues.Integer(LegajoJsonPointer.Get(effective, "/totals/totalBienes/page")));
        Assert.Equal(260_000m, LegajoJsonValues.Decimal(LegajoJsonPointer.Get(effective, "/items/1/fiscalValue")));
        Assert.Equal(new[] { "/items/1/fiscalValue", "/totals/totalBienes" }.Order(), corrected.Order());
        Assert.Equal(resultJson, BpResultJson());   // the run's JSON is untouched
    }

    [Fact]
    public void Apply_NullClears_AppendAddsARow_UnresolvablePathIsSkipped()
    {
        var row = """{"rubro":"Creditos","location":"Pais","description":"Prestamo","matchKey":null,"ownershipPct":100,"acquisitionDate":null,"fiscalValue":5000,"currency":"ARS","isExempt":false,"isHousing":false,"page":null,"quote":null,"confidence":1}""";

        var (effective, corrected) = LegajoEffectiveValues.Apply(Bp, BpResultJson(),
        [
            Correction("/totals/bienesExentos", null, 3),
            Correction("/items/4", row, 2),
            Correction("/items/99/fiscalValue", "1", 1)
        ]);

        Assert.Null(LegajoJsonPointer.Get(effective, "/totals/bienesExentos/value"));
        Assert.Equal(5, LegajoJsonValues.ArrayAt(effective, "/items").Count);
        Assert.DoesNotContain("/items/99/fiscalValue", corrected);
    }

    // ── Validation ──────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_Field_NormalizesAndReportsTheOriginal()
    {
        var result = Validate("/totals/totalBienes", "1800000.5");

        Assert.True(result.IsValid);
        Assert.Equal(1_800_000.5m, LegajoJsonValues.Decimal(result.Value));
        Assert.Equal(1_700_000.50m, LegajoJsonValues.Decimal(result.Original));
    }

    [Theory]
    [InlineData("/totals/totalBienes", "\"1.800.000\"")]
    [InlineData("/header/fiscalYear", "2025.5")]
    [InlineData("/items/0/acquisitionDate", "\"15/03/2020\"")]
    [InlineData("/items/0/rubro", "\"Yates\"")]
    [InlineData("/items/0/rubro", "null")]
    [InlineData("/items/0/isExempt", "\"si\"")]
    public void Validate_WrongType_400(string path, string json)
        => Assert.False(Validate(path, json).IsValid);

    [Theory]
    [InlineData("/totals/nope")]
    [InlineData("/items/9/fiscalValue")]
    [InlineData("/totals")]
    [InlineData("/items")]
    [InlineData("/items/0")]
    [InlineData("/totals/totalBienes/value")]
    [InlineData("/totals/totalBienes/page")]
    [InlineData("/items/0/confidence")]
    [InlineData("totals/totalBienes")]
    [InlineData("")]
    [InlineData(null)]
    public void Validate_PathNotCorrectable_400(string? path)
        => Assert.False(Validate(path, "1").IsValid);

    [Fact]
    public void Validate_HolderCuit_IsNeverCorrectable()
    {
        var result = Validate(LegajoExtractionVocabulary.HolderCuitPath, "\"20123456786\"");

        Assert.False(result.IsValid);
        Assert.Contains("CUIT", result.Error);
    }

    [Fact]
    public void Validate_MatchKeyMembers_AndTheWholeMatchKey()
    {
        Assert.True(Validate("/items/0/matchKey/value", "\"PART-0002\"").IsValid);
        Assert.True(Validate("/items/0/matchKey/kind", "\"Dominio\"").IsValid);
        Assert.True(Validate("/items/0/matchKey", """{"kind":"Cbu","value":"000"}""").IsValid);
        Assert.True(Validate("/items/0/matchKey", "null").IsValid);
        Assert.False(Validate("/items/0/matchKey", """{"kind":"Nope","value":null}""").IsValid);
    }

    [Fact]
    public void Validate_AppendRow_AtTheArrayLengthOnly_IsFullyTyped_AndTrusted()
    {
        var row = """{"rubro":"Creditos","location":"Pais","description":"Prestamo","matchKey":null,"ownershipPct":100,"acquisitionDate":null,"fiscalValue":5000,"currency":"ARS","isExempt":false,"isHousing":false}""";

        var appended = Validate("/items/4", row);
        Assert.True(appended.IsValid);
        Assert.Equal(1m, LegajoJsonValues.Decimal(appended.Value!["confidence"]));

        Assert.False(Validate("/items/5", row).IsValid);
        Assert.False(Validate("/items/4", """{"rubro":"Yates"}""").IsValid);
    }

    [Fact]
    public void Validate_MissingValue_400_NullClears()
    {
        var missing = LegajoCorrectionValidator.Validate(Bp, JsonNode.Parse(BpResultJson())!.AsObject(),
            "/totals/totalBienes", default);

        Assert.False(missing.IsValid);
        Assert.True(Validate("/totals/totalBienes", "null").IsValid);
    }

    [Fact]
    public void Validate_TextOverItsMaxLength_Rejected_NeverTruncated()
    {
        var atMax = Validate("/items/1/description", "\"" + new string('x', 200) + "\"");
        Assert.True(atMax.IsValid);
        Assert.Equal(200, atMax.Value!.GetValue<string>().Length);

        var over = Validate("/items/1/description", "\"" + new string('x', 201) + "\"");
        Assert.False(over.IsValid);
        Assert.Equal("/items/1/description supera 200 caracteres.", over.Error);
    }

    [Fact]
    public void Validate_AppendedRow_WithAnOverLongTextOrQuote_Rejected()
    {
        string Row(string description, string quote)
            => $$"""{"rubro":"Creditos","location":"Pais","description":"{{description}}","matchKey":null,"ownershipPct":100,"acquisitionDate":null,"fiscalValue":5000,"currency":"ARS","isExempt":false,"isHousing":false,"page":1,"quote":"{{quote}}"}""";

        Assert.True(Validate("/items/4", Row("Prestamo", "Prestamo")).IsValid);

        var description = Validate("/items/4", Row(new string('d', 201), "Prestamo"));
        Assert.False(description.IsValid);
        Assert.Contains("/items/4/description: too_long", description.Error);

        var quote = Validate("/items/4", Row("Prestamo", new string('q', LegajoToolInputReader.MaxQuoteLength + 1)));
        Assert.False(quote.IsValid);
        Assert.Contains("/items/4/quote: too_long", quote.Error);
    }

    [Fact]
    public void Reader_ModelTextOverItsMaxLength_IsStillTruncated()
    {
        using var document = JsonDocument.Parse("\"" + new string('x', 250) + "\"");
        var violations = new List<string>();

        var value = LegajoToolInputReader.ReadValue(LegajoValueSpec.Text(200), document.RootElement, "/x", violations);

        Assert.Empty(violations);
        Assert.Equal(200, value!.GetValue<string>().Length);
    }

    [Fact]
    public void Validate_Messages_QuoteThePath_NeverTheValue()
    {
        var result = Validate("/totals/totalBienes", "\"SECRETO-12345\"");

        Assert.Contains("/totals/totalBienes", result.Error);
        Assert.DoesNotContain("SECRETO", result.Error);
    }
}
