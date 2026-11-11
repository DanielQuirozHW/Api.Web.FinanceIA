// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-31

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Settings;
using Xunit;
using static Api.Web.FinanceIA.Tests.PersonAnalysis.ExpectedDocumentsTestKit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// <see cref="PersonExpectedDocumentsMatrix"/>: every validation rule of the tenant's expected-documents matrix,
/// with its es-AR message and field path, and the stored JSON's round trip and parse.
/// </summary>
public sealed class PersonExpectedDocumentsMatrixTests
{
    private const string Mono = ExpectedDocumentRegimes.Monotributo;

    private static Dictionary<string, List<PersonExpectedDocumentLineDto?>?> Matrix(
        string regime, params PersonExpectedDocumentLineDto?[] lines)
        => new() { [regime] = lines.ToList() };

    /// <summary>The consent + one required document: the smallest valid regime.</summary>
    private static PersonExpectedDocumentLineDto?[] Minimal(params PersonExpectedDocumentLineDto?[] more)
        => [Line(DocumentTypes.AutorizacionDatos), Line(DocumentTypes.ManifestacionBienes), .. more];

    private static ValidationError Single(Dictionary<string, List<PersonExpectedDocumentLineDto?>?>? matrix)
        => Assert.Single(PersonExpectedDocumentsMatrix.Validate(matrix));

    // ── Valid ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheCustomMatrix_AndTheMinimalRegime_AreValid()
    {
        Assert.Empty(PersonExpectedDocumentsMatrix.Validate(Matrix(Mono, CustomMonotributo().ToArray())));
        Assert.Empty(PersonExpectedDocumentsMatrix.Validate(Matrix(ExpectedDocumentRegimes.ExentoOrDesconocido, Minimal())));
    }

    [Fact]
    public void EveryCodeDefault_IsAValidTenantMatrix()
    {
        var matrix = LegajoExpectedDocuments.Defaults.ToDictionary(
            r => r.Key,
            r => (List<PersonExpectedDocumentLineDto?>?)r.Value
                .Select(l => Line(l.DocumentType, l.Required, null, l.Alternatives.ToArray()))
                .ToList());

        Assert.Empty(PersonExpectedDocumentsMatrix.Validate(matrix));
    }

    // ── Regimes ────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoRegime_IsRefused()
    {
        Assert.Equal(new ValidationError("regimes", "Se requiere al menos un régimen."), Single(null));
        Assert.Equal(new ValidationError("regimes", "Se requiere al menos un régimen."), Single([]));
    }

    [Theory]
    [InlineData("Exento")]          // a fiscal regime, not a bucket
    [InlineData("monotributo")]     // exact spelling
    public void AnUnknownRegime_IsRefused(string regime)
    {
        var error = Single(Matrix(regime, Minimal()));

        Assert.Equal($"regimes.{regime}", error.Field);
        Assert.Equal("Régimen desconocido. Los regímenes válidos son Monotributo, ResponsableInscripto y ExentoOrDesconocido.", error.Message);
    }

    [Fact]
    public void AnEmptyRegime_IsRefused()
        => Assert.Equal(new ValidationError("regimes.Monotributo", "El régimen necesita al menos un documento."), Single(Matrix(Mono)));

    // ── Document type ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DocumentTypes.Otro)]
    [InlineData(DocumentTypes.Balance)]
    [InlineData("ganancias")]
    public void ANonConfigurableType_IsRefused(string type)
    {
        Assert.Equal(
            new ValidationError("regimes.Monotributo[2].documentType", "El tipo de documento no es uno de los tipos configurables del legajo."),
            Single(Matrix(Mono, Minimal(Line(type)))));
    }

    [Fact]
    public void AMissingType_IsRefused()
    {
        Assert.Equal(
            new ValidationError("regimes.Monotributo[2].documentType", "Se requiere el tipo de documento."),
            Single(Matrix(Mono, Minimal(new PersonExpectedDocumentLineDto { Requirement = "optional" }))));
    }

    [Fact]
    public void ARepeatedType_IsRefused()
    {
        Assert.Equal(
            new ValidationError("regimes.Monotributo[2].documentType", "El tipo de documento está repetido en el régimen."),
            Single(Matrix(Mono, Minimal(Line(DocumentTypes.ManifestacionBienes, required: false)))));
    }

    [Fact]
    public void ANullLine_IsRefused()
        => Assert.Equal(new ValidationError("regimes.Monotributo[2]", "La línea está vacía."), Single(Matrix(Mono, Minimal((PersonExpectedDocumentLineDto?)null))));

    // ── Requirement ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("Required")]
    [InlineData("no_se_pide")]
    public void AnUnknownRequirement_IsRefused(string? requirement)
    {
        Assert.Equal(
            new ValidationError("regimes.Monotributo[2].requirement", "El requisito debe ser \"required\" (obligatorio) u \"optional\" (opcional)."),
            Single(Matrix(Mono, Minimal(new PersonExpectedDocumentLineDto { DocumentType = DocumentTypes.Ganancias, Requirement = requirement }))));
    }

    // ── Alternatives ───────────────────────────────────────────────────────────────

    [Fact]
    public void AnAlternative_OutsideTheConfigurableTypes_IsRefused()
    {
        Assert.Equal(
            new ValidationError("regimes.Monotributo[2].alternatives[0]", "La alternativa no es uno de los tipos configurables del legajo."),
            Single(Matrix(Mono, Minimal(Line(DocumentTypes.BienesPersonales, true, null, DocumentTypes.Otro)))));
    }

    [Fact]
    public void AnAlternative_ThatIsTheLinesOwnType_IsRefused()
    {
        Assert.Equal(
            new ValidationError("regimes.Monotributo[2].alternatives[1]", "Un documento no puede ser alternativa de sí mismo."),
            Single(Matrix(Mono, Minimal(Line(DocumentTypes.BienesPersonales, true, null, DocumentTypes.Reibp, DocumentTypes.BienesPersonales)))));
    }

    [Fact]
    public void AnAlternative_ThatIsAnotherLine_IsRefused_WhereverThatLineIs()
    {
        // ManifestacionBienes is line [1], before the alternative; Ganancias is line [3], after it.
        var errors = PersonExpectedDocumentsMatrix.Validate(Matrix(Mono, Minimal(
            Line(DocumentTypes.BienesPersonales, true, null, DocumentTypes.ManifestacionBienes, DocumentTypes.Ganancias),
            Line(DocumentTypes.Ganancias))));

        Assert.Equal(
            [
                new ValidationError("regimes.Monotributo[2].alternatives[0]", "La alternativa ya es un documento del régimen."),
                new ValidationError("regimes.Monotributo[2].alternatives[1]", "La alternativa ya es un documento del régimen.")
            ],
            errors);
    }

    [Fact]
    public void AnAlternative_RepeatedAcrossLines_IsRefused()
    {
        Assert.Equal(
            new ValidationError("regimes.Monotributo[3].alternatives[0]", "La alternativa ya figura en otra línea del régimen."),
            Single(Matrix(Mono, Minimal(
                Line(DocumentTypes.BienesPersonales, true, null, DocumentTypes.Reibp),
                Line(DocumentTypes.Ganancias, true, null, DocumentTypes.Reibp)))));
    }

    // ── Consent invariant ──────────────────────────────────────────────────────────

    [Fact]
    public void WithoutTheAuthorization_TheRegimeIsRefused()
    {
        Assert.Equal(
            new ValidationError("regimes.Monotributo", "La autorización de datos (AutorizacionDatos) debe ser obligatoria en todos los regímenes."),
            Single(Matrix(Mono, Line(DocumentTypes.ManifestacionBienes), Line(DocumentTypes.Ganancias))));
    }

    [Fact]
    public void AnOptionalAuthorization_IsRefused()
    {
        Assert.Equal(
            new ValidationError("regimes.Monotributo[0].requirement", "La autorización de datos (AutorizacionDatos) debe ser obligatoria en todos los regímenes."),
            Single(Matrix(Mono, Line(DocumentTypes.AutorizacionDatos, required: false), Line(DocumentTypes.ManifestacionBienes))));
    }

    [Fact]
    public void OnlyTheAuthorizationRequired_IsRefused()
    {
        Assert.Equal(
            new ValidationError("regimes.Monotributo", "El régimen necesita al menos un documento obligatorio además de la autorización de datos."),
            Single(Matrix(Mono, Line(DocumentTypes.AutorizacionDatos), Line(DocumentTypes.ManifestacionBienes, required: false))));
    }

    // ── Max age ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(60)]
    public void MaxAge_AtTheBounds_IsValid(int months)
        => Assert.Empty(PersonExpectedDocumentsMatrix.Validate(Matrix(Mono, Minimal(Line(DocumentTypes.Ganancias, true, months)))));

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    [InlineData(-3)]
    [InlineData(6.5)]
    public void MaxAge_OutOfRangeOrFractional_IsRefused(double months)
    {
        Assert.Equal(
            new ValidationError("regimes.Monotributo[2].maxAgeMonths", "La antigüedad máxima debe ser un número entero de meses entre 1 y 60."),
            Single(Matrix(Mono, Minimal(Line(DocumentTypes.Ganancias, true, (decimal)months)))));
    }

    [Theory]
    [InlineData(DocumentTypes.AutorizacionDatos, 0)]
    [InlineData(DocumentTypes.FlujoProyectado, 2)]
    public void MaxAge_OnATimelessType_IsRefused(string type, int index)
    {
        PersonExpectedDocumentLineDto?[] lines = type == DocumentTypes.AutorizacionDatos
            ? [Line(DocumentTypes.AutorizacionDatos, true, 6), Line(DocumentTypes.ManifestacionBienes)]
            : Minimal(Line(DocumentTypes.FlujoProyectado, false, 6));

        Assert.Equal(
            new ValidationError($"regimes.Monotributo[{index}].maxAgeMonths", "La autorización de datos y el flujo proyectado no tienen antigüedad máxima."),
            Single(Matrix(Mono, lines)));
    }

    [Fact]
    public void Errors_OfSeveralRegimes_AreAllReported()
    {
        var errors = PersonExpectedDocumentsMatrix.Validate(new Dictionary<string, List<PersonExpectedDocumentLineDto?>?>
        {
            [Mono]                                       = [Line(DocumentTypes.ManifestacionBienes)],
            [ExpectedDocumentRegimes.ResponsableInscripto] = [.. Minimal(Line(DocumentTypes.Otro))]
        });

        Assert.Equal(["regimes.Monotributo", "regimes.ResponsableInscripto[2].documentType"], errors.Select(e => e.Field));
    }

    // ── Stored JSON ────────────────────────────────────────────────────────────────

    [Fact]
    public void Serialize_ThenParse_RoundTrips()
    {
        var json = CustomMonotributoJson();

        Assert.True(PersonExpectedDocumentsMatrix.TryParseStored(json, out var regimes, out var kind));
        Assert.Null(kind);

        var lines = Assert.Single(regimes).Value;
        Assert.Equal(CustomMonotributo().Select(l => l!.DocumentType), lines.Select(l => l.DocumentType));
        var certificacion = lines.Single(l => l.DocumentType == DocumentTypes.CertificacionIngresos);
        Assert.False(certificacion.Required);
        Assert.Equal(3, certificacion.MaxAgeMonths);
        Assert.Equal([DocumentTypes.Ganancias], lines.Single(l => l.DocumentType == DocumentTypes.BienesPersonales).Alternatives);
        Assert.Contains("\"maxAgeMonths\":12", json);
        Assert.Contains("\"requirement\":\"optional\"", json);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    [InlineData("")]
    public void AnUnparseableRow_IsNotUsed(string json)
    {
        Assert.False(PersonExpectedDocumentsMatrix.TryParseStored(json, out var regimes, out var kind));
        Assert.Equal(PersonExpectedDocumentsMatrix.ErrorUnparseable, kind);
        Assert.Empty(regimes);
    }

    [Fact]
    public void AnInvalidRow_IsNotUsed_AtAll()
    {
        // Monotributo is fine, ResponsableInscripto lacks the consent: NO regime of the row is used.
        const string json = """
            {"regimes":{
              "Monotributo":[{"documentType":"AutorizacionDatos","requirement":"required"},{"documentType":"Monotributo","requirement":"required"}],
              "ResponsableInscripto":[{"documentType":"Ganancias","requirement":"required"}]}}
            """;

        Assert.False(PersonExpectedDocumentsMatrix.TryParseStored(json, out var regimes, out var kind));
        Assert.Equal(PersonExpectedDocumentsMatrix.ErrorInvalid, kind);
        Assert.Empty(regimes);
    }
}
