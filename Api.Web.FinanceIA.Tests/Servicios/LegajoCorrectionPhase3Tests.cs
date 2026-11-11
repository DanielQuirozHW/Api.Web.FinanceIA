// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-01

using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// <see cref="LegajoCorrectionValidator"/> and <see cref="LegajoEffectiveValues"/> on the Phase 3a schemas.
/// Nothing in the validator is type-specific: the schemas alone decide what is correctable and how a
/// value is typed (the only addition is the message of the new Period kind). Synthetic data.
/// </summary>
public sealed class LegajoCorrectionPhase3Tests
{
    private static LegajoCorrectionValidator.Result Validate(string documentType, string path, string json,
                                                             Action<JsonObject>? mutate = null)
    {
        using var document = JsonDocument.Parse(json);
        return LegajoCorrectionValidator.Validate(LegajoExtractionFixtures.Schemas[documentType],
            LegajoExtractionFixtures.NormalizedClean(documentType, mutate), path, document.RootElement.Clone());
    }

    // ── Accepted: fields, row values, nested objects, appended rows ─────────────

    [Theory]
    [InlineData(DocumentTypes.Ganancias, "/totals/gananciaNeta", "12100000", "12100000")]
    [InlineData(DocumentTypes.Ganancias, "/totals/saldoSide", "\"contribuyente\"", "\"Contribuyente\"")]
    [InlineData(DocumentTypes.Ganancias, "/justification/montoConsumido", "4800000.5", "4800000.5")]
    [InlineData(DocumentTypes.Ganancias, "/justification/lines/1/amount", "510000", "510000")]
    [InlineData(DocumentTypes.Ganancias, "/categoryResults/4/amount", "-60000", "-60000")]
    [InlineData(DocumentTypes.Ganancias, "/categoryResults/3/isCedular", "false", "false")]
    [InlineData(DocumentTypes.DdjjIva, "/periods/0/period", "\"2025-10\"", "\"2025-10\"")]
    [InlineData(DocumentTypes.DdjjIva, "/periods/0/debitoFiscal", "231000", "231000")]
    [InlineData(DocumentTypes.DdjjIva, "/periods/0/saldoTecnico/side", "\"Contribuyente\"", "\"Contribuyente\"")]
    [InlineData(DocumentTypes.DdjjIva, "/alicuotaLines/1/alicuotaPct", "10.5", "10.5")]
    [InlineData(DocumentTypes.DdjjIibb, "/header/regimen", "\"Local\"", "\"Local\"")]
    [InlineData(DocumentTypes.DdjjIibb, "/header/fiscalYear", "2025", "2025")]
    [InlineData(DocumentTypes.DdjjIibb, "/rows/2/impuestoDeterminado", "42001", "42001")]
    [InlineData(DocumentTypes.CertificacionIngresos, "/header/periodTo", "\"2025-11-30\"", "\"2025-11-30\"")]
    [InlineData(DocumentTypes.CertificacionIngresos, "/header/accountant/matricula", "\"T 201 F 41\"", "\"T 201 F 41\"")]
    [InlineData(DocumentTypes.CertificacionIngresos, "/rows/0/period", "null", "null")]
    [InlineData(DocumentTypes.Monotributo, "/header/categoria", "\"e\"", "\"E\"")]
    [InlineData(DocumentTypes.Monotributo, "/payments/2/paymentDate", "\"2025-12-20\"", "\"2025-12-20\"")]
    public void Valid_IsNormalized_LikeTheModelsAnswer(string documentType, string path, string json, string normalized)
    {
        var result = Validate(documentType, path, json);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(normalized, result.Value?.ToJsonString() ?? "null");
    }

    [Fact]
    public void Iva_SaldoTecnico_IsReplacedWhole_OrCleared()
    {
        var whole = Validate(DocumentTypes.DdjjIva, "/periods/1/saldoTecnico", """{"amount":42000,"side":"contribuyente"}""");
        Assert.True(whole.IsValid, whole.Error);
        Assert.Equal("Contribuyente", whole.Value!["side"]!.GetValue<string>());

        Assert.True(Validate(DocumentTypes.DdjjIva, "/periods/1/saldoTecnico", "null").IsValid);

        var noSide = Validate(DocumentTypes.DdjjIva, "/periods/1/saldoTecnico", """{"amount":42000,"side":null}""");
        Assert.False(noSide.IsValid);
    }

    public static TheoryData<string, string, string> AppendedRows => new()
    {
        { DocumentTypes.Ganancias, "/justification/lines/4",
          """{"side":"Aplicacion","label":"Donaciones","amount":10000,"page":5,"quote":"Donaciones 10.000","confidence":0.2}""" },
        { DocumentTypes.Ganancias, "/categoryResults/5",
          """{"category":"Cuarta","source":"Argentina","isCedular":false,"label":"Sueldos","amount":100,"page":null,"quote":null,"confidence":0}""" },
        { DocumentTypes.DdjjIva, "/alicuotaLines/6",
          """{"period":"2025-12","rectificativaNumber":null,"side":"Debito","label":"Ventas 27","alicuotaPct":27,"netoGravado":100,"impuesto":27,"page":2,"quote":null,"confidence":0.5}""" },
        { DocumentTypes.DdjjIibb, "/rows/4",
          """{"period":"2025-12","rectificativaNumber":null,"jurisdiction":"Cordoba","activity":"620100","baseImponible":100,"alicuotaPct":4,"impuestoDeterminado":4,"page":2,"quote":null,"confidence":0.5}""" },
        { DocumentTypes.CertificacionIngresos, "/rows/3",
          """{"period":null,"concept":"Alquileres","amount":10,"page":1,"quote":null,"confidence":0.5}""" },
        { DocumentTypes.Monotributo, "/payments/3",
          """{"period":"2026-01","amount":42000.5,"paymentDate":null,"page":3,"quote":null,"confidence":0.5}""" }
    };

    [Theory]
    [MemberData(nameof(AppendedRows))]
    public void AppendRow_AtTheArrayLength_IsFullyTyped_AndTrusted(string documentType, string path, string row)
    {
        var result = Validate(documentType, path, row);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(1m, LegajoJsonValues.Decimal(result.Value!["confidence"]));
    }

    // ── Rejected ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DocumentTypes.DdjjIva, "/periods/0/period", "\"11/2025\"", "/periods/0/period debe ser un período AAAA-MM o null.")]
    [InlineData(DocumentTypes.DdjjIibb, "/rows/0/period", "\"2025\"", "/rows/0/period debe ser un período AAAA-MM o null.")]
    [InlineData(DocumentTypes.Monotributo, "/header/categoria", "\"L\"", "/header/categoria debe ser uno de: A, B, C, D, E, F, G, H, I, J, K.")]
    [InlineData(DocumentTypes.Ganancias, "/categoryResults/0/category", "\"Quinta\"",
                "/categoryResults/0/category debe ser uno de: Primera, Segunda, Tercera, Cuarta.")]
    [InlineData(DocumentTypes.DdjjIva, "/alicuotaLines/0/side", "\"Ambos\"", "/alicuotaLines/0/side debe ser uno de: Debito, Credito.")]
    [InlineData(DocumentTypes.CertificacionIngresos, "/header/periodFrom", "\"2025-10\"", "/header/periodFrom debe ser una fecha AAAA-MM-DD o null.")]
    [InlineData(DocumentTypes.Ganancias, "/justification", "{}", "/justification es una sección, no un campo; corrija sus campos uno por uno.")]
    [InlineData(DocumentTypes.DdjjIva, "/periods/5/debitoFiscal", "1", "/periods/5 no existe en esta extracción.")]
    [InlineData(DocumentTypes.DdjjIva, "/periods/0", "{}",
                "Una fila existente no se reemplaza entera: corrija sus campos. Para agregar una fila use /periods/2.")]
    [InlineData(DocumentTypes.Monotributo, "/header/holderCuit", "\"20123456786\"", "El CUIT del titular no se corrige: se compara con el del legajo.")]
    public void Invalid_400_WithAMessageThatQuotesThePathOnly(string documentType, string path, string json, string message)
    {
        var result = Validate(documentType, path, json);

        Assert.Equal(message, result.Error);
    }

    [Fact]
    public void Ganancias_WithoutJustification_ItsFieldsAreNotCorrectable()
    {
        var result = Validate(DocumentTypes.Ganancias, "/justification/montoConsumido", "1", i => i["justification"] = null);

        Assert.Equal("/justification/montoConsumido no existe en esta extracción.", result.Error);
    }

    // ── Effective values on a new schema ────────────────────────────────────────

    [Fact]
    public void Apply_OnIva_RowValue_NestedObject_AndAppendedRow()
    {
        var schema = LegajoExtractionPrompts.DdjjIva;
        var resultJson = LegajoExtractionFixtures.NormalizedClean(DocumentTypes.DdjjIva).ToJsonString();
        LegajoCorrection Correction(string path, string? json, int minutesAgo) => new()
        {
            CorrectionId = Guid.NewGuid(), TenantId = LegajoTestData.Tenant, ExtractionId = Guid.NewGuid(), FieldPath = path,
            CorrectedValueJson = json, Reason = "Leido del original", CorrectedBy = Guid.NewGuid(),
            CorrectedAt = DateTime.UtcNow.AddMinutes(-minutesAgo)
        };

        var (effective, corrected) = LegajoEffectiveValues.Apply(schema, resultJson,
        [
            Correction("/periods/0/period", "\"2025-10\"", 3),
            Correction("/periods/1/saldoTecnico", null, 2),
            Correction("/alicuotaLines/6",
                """{"period":"2025-12","rectificativaNumber":null,"side":"Credito","label":"Compras 10,5","alicuotaPct":10.5,"netoGravado":100,"impuesto":10.5,"page":null,"quote":null,"confidence":1}""", 1)
        ]);

        Assert.Equal("2025-10", LegajoJsonValues.String(LegajoJsonPointer.Get(effective, "/periods/0/period")));
        Assert.Null(LegajoJsonPointer.Get(effective, "/periods/1/saldoTecnico"));
        Assert.Equal(7, LegajoJsonValues.ArrayAt(effective, "/alicuotaLines").Count);
        Assert.Equal(3, corrected.Count);
        Assert.Equal(resultJson, LegajoExtractionFixtures.NormalizedClean(DocumentTypes.DdjjIva).ToJsonString());
    }
}
