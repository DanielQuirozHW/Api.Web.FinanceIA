// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-31

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using Xunit;
using static Api.Web.FinanceIA.Tests.PersonAnalysis.PersonEvaluationFixtures;

namespace Api.Web.FinanceIA.Tests.PersonAnalysis;

/// <summary>
/// Engine 0.10: the coverage index measures against the TENANT's expected-documents matrix
/// (<see cref="PersonEvaluationInput.ExpectedDocuments"/>) — its required lines, its alternatives, the types it
/// leaves out and its per-line recency cuts — and the sources freeze it, while a blob frozen before 0.10 reads as
/// it was frozen.
/// </summary>
public sealed class PersonEvaluationCoverageMatrixTests
{
    /// <summary>
    /// A responsable inscripto matrix: Ganancias with a 24-month cut, DDJJ IVA with a 3-month cut, Bienes
    /// Personales or REIBP or Manifestación, and NO DDJJ IIBB nor certificación de ingresos ("no se pide").
    /// </summary>
    private static readonly IReadOnlyList<LegajoExpectedDocument> TenantRi =
    [
        new(DocumentTypes.AutorizacionDatos, true, []),
        new(DocumentTypes.Ganancias, true, [], MaxAgeMonths: 24),
        new(DocumentTypes.DdjjIva, true, [], MaxAgeMonths: 3),
        new(DocumentTypes.BienesPersonales, true, [DocumentTypes.Reibp, DocumentTypes.ManifestacionBienes]),
        new(DocumentTypes.FlujoProyectado, false, [])
    ];

    private static PersonCoverageResult CoverageOf(PersonEvaluationInput input)
        => PersonEvaluationService.Evaluate(input).Coverage;

    private static PersonCoverageItem Item(PersonEvaluationInput input, string type)
        => Assert.Single(CoverageOf(input).Items, i => i.DocumentType == type);

    [Fact]
    public void TenantMatrix_DrivesRequiredTotal_Alternatives_AndOrder()
    {
        var input = Empty(FiscalRegimes.ResponsableInscripto) with
        {
            ExpectedDocuments = TenantRi,
            Documents         = [Doc(DocumentTypes.AutorizacionDatos), Doc(DocumentTypes.ManifestacionBienes, periodEnd: new DateOnly(2026, 9, 30))]
        };

        var coverage = CoverageOf(input);

        Assert.Equal(TenantRi.Select(l => l.DocumentType), coverage.Items.Select(i => i.DocumentType));
        Assert.Equal(4, coverage.RequiredTotal);                           // the default RI matrix requires 7
        Assert.Equal(2, coverage.RequiredPresent);                         // consent + Bienes Personales via the manifestación
        Assert.Equal(0.5m, coverage.Index);

        var bienes = Assert.Single(coverage.Items, i => i.DocumentType == DocumentTypes.BienesPersonales);
        Assert.Equal([DocumentTypes.Reibp, DocumentTypes.ManifestacionBienes], bienes.Alternatives);
        Assert.True(bienes.Present);
    }

    [Fact]
    public void ATypeTheTenantDoesNotAskFor_DisappearsFromTheIndex_EvenWhenUploaded()
    {
        var input = Empty(FiscalRegimes.ResponsableInscripto) with
        {
            ExpectedDocuments = TenantRi,
            Documents         = [Doc(DocumentTypes.DdjjIibb, periodEnd: new DateOnly(2026, 9, 30))]
        };

        var coverage = CoverageOf(input);

        Assert.DoesNotContain(coverage.Items, i => i.DocumentType == DocumentTypes.DdjjIibb);
        Assert.DoesNotContain(coverage.Items, i => i.DocumentType == DocumentTypes.CertificacionIngresos);
        Assert.Equal(0, coverage.RequiredPresent);
    }

    [Fact]
    public void ALineCut_OverridesTheClassKey_InBothDirections()
    {
        var input = Empty(FiscalRegimes.ResponsableInscripto) with
        {
            ExpectedDocuments = TenantRi,
            Documents =
            [
                Doc(DocumentTypes.Ganancias, periodEnd: new DateOnly(2024, 12, 31)),   // 21 months: stale at the default 18
                Doc(DocumentTypes.DdjjIva, periodEnd: new DateOnly(2026, 5, 31))       // 4 months: fresh at the default 6
            ]
        };

        var ganancias = Item(input, DocumentTypes.Ganancias);
        Assert.Equal(21, ganancias.AgeMonths);
        Assert.Equal(24, ganancias.MaxAgeMonths);
        Assert.False(ganancias.IsStale);

        var iva = Item(input, DocumentTypes.DdjjIva);
        Assert.Equal(4, iva.AgeMonths);
        Assert.Equal(3, iva.MaxAgeMonths);
        Assert.True(iva.IsStale);

        // The same documents on the default matrix: the class keys.
        var byDefault = input with { ExpectedDocuments = null };
        Assert.True(Item(byDefault, DocumentTypes.Ganancias).IsStale);
        Assert.Equal(18, Item(byDefault, DocumentTypes.Ganancias).MaxAgeMonths);
        Assert.False(Item(byDefault, DocumentTypes.DdjjIva).IsStale);
        Assert.Equal(6, Item(byDefault, DocumentTypes.DdjjIva).MaxAgeMonths);
    }

    [Fact]
    public void WithoutALineCut_TheTenantsClassKeyApplies()
    {
        var input = Empty(FiscalRegimes.ResponsableInscripto) with
        {
            ExpectedDocuments = TenantRi,
            Parameters        = PersonEvaluationParameters.Defaults with { DdjjAnualAntiguedadMaxMeses = 12 },
            Documents         = [Doc(DocumentTypes.BienesPersonales, periodEnd: new DateOnly(2025, 6, 30))]
        };

        var bienes = Item(input, DocumentTypes.BienesPersonales);

        Assert.Equal(12, bienes.MaxAgeMonths);
        Assert.True(bienes.IsStale);
    }

    [Fact]
    public void ATimelessType_NeverHasACut_EvenIfALineStatesOne()
    {
        var input = Empty() with
        {
            ExpectedDocuments = [new(DocumentTypes.AutorizacionDatos, true, [], MaxAgeMonths: 5), .. TenantRi.Skip(1)],
            Documents         = [Doc(DocumentTypes.AutorizacionDatos)]
        };

        Assert.Null(Item(input, DocumentTypes.AutorizacionDatos).MaxAgeMonths);
    }

    [Fact]
    public void WithoutTenantLines_TheCodeDefaultApplies()
    {
        var withNull    = CoverageOf(Empty(FiscalRegimes.Monotributo));
        var withDefault = CoverageOf(Empty(FiscalRegimes.Monotributo) with
        {
            ExpectedDocuments = LegajoExpectedDocuments.For(FiscalRegimes.Monotributo)
        });

        Assert.Equal(PersonEvaluationJson.Serialize(withNull), PersonEvaluationJson.Serialize(withDefault));
    }

    [Fact]
    public void RegimenDesconocido_IsStillStated_OnATenantMatrix()
    {
        var input = Empty(FiscalRegimes.Desconocido) with
        {
            ExpectedDocuments =
            [
                new(DocumentTypes.AutorizacionDatos, true, []),
                new(DocumentTypes.ManifestacionBienes, true, [], MaxAgeMonths: 12)
            ]
        };

        var coverage = CoverageOf(input);

        Assert.Contains(coverage.CannotConclude, c => c.Code == "regimen_desconocido");
        Assert.Equal(2, coverage.RequiredTotal);
    }

    // ── Freezing ───────────────────────────────────────────────────────────────────

    private static PersonEvaluationSources SourcesWith(PersonExpectedDocumentsForRegime? expected)
        => PersonEvaluationService.Sources(
            [], new Dictionary<Guid, LegajoExtraction>(), [], new PersonLegajo { PersonLegajoId = LegajoId, TenantId = Tenant },
            null, Inventory(), incomeRows: 0, macroFetchedAt: null, macroSource: null,
            PersonEvaluationParameters.Defaults, agro: null, expectedDocuments: expected);

    [Fact]
    public void Sources_FreezeTheMatrix_OutsideTheParameters()
    {
        var updatedAt = new DateTime(2026, 10, 30, 15, 0, 0, DateTimeKind.Utc);
        var expected  = new PersonExpectedDocumentsForRegime
        {
            Regime    = ExpectedDocumentRegimes.ResponsableInscripto,
            Source    = ExpectedDocumentsSources.Tenant,
            UpdatedAt = updatedAt,
            Lines     = TenantRi
        };

        var json = JsonNode.Parse(PersonEvaluationJson.Serialize(SourcesWith(expected)))!.AsObject();

        var frozen = json["expectedDocuments"]!.AsObject();
        Assert.Equal("tenant", (string?)frozen["source"]);
        Assert.Equal("ResponsableInscripto", (string?)frozen["regime"]);
        Assert.Equal(updatedAt, (DateTime?)frozen["updatedAt"]);
        var lines = frozen["lines"]!.AsArray();
        Assert.Equal(5, lines.Count);
        Assert.Equal(24, (int?)lines[1]!["maxAgeMonths"]);
        Assert.False(lines[0]!.AsObject().ContainsKey("maxAgeMonths"));   // null cut left out
        Assert.False(json["parameters"]!.AsObject().ContainsKey("expectedDocuments"));

        var back = PersonEvaluationJson.TryDeserialize<PersonEvaluationSources>(json.ToJsonString())!;
        Assert.Equal(TenantRi, back.ExpectedDocuments!.Lines, LineComparer.Instance);
        Assert.Equal(updatedAt, back.ExpectedDocuments.UpdatedAt);
    }

    [Fact]
    public void Sources_WithoutAMatrix_WriteNoMember()
    {
        var json = JsonNode.Parse(PersonEvaluationJson.Serialize(SourcesWith(null)))!.AsObject();

        Assert.False(json.ContainsKey("expectedDocuments"));
    }

    [Fact]
    public void APre010Blob_ReadsWithoutAMatrix_AndRoundTripsByteForByte()
    {
        var pre010 = PersonEvaluationJson.Serialize(SourcesWith(null));   // what 0.9 wrote: no expectedDocuments

        var sources = PersonEvaluationJson.TryDeserialize<PersonEvaluationSources>(pre010);

        Assert.NotNull(sources);
        Assert.Null(sources.ExpectedDocuments);
        Assert.Equal(pre010, PersonEvaluationJson.Serialize(sources));
        Assert.Null(PersonEvaluationJson.TryDeserialize<PersonEvaluationSources>(PersonEvaluationPre06Fixtures.SourcesJson())!.ExpectedDocuments);
    }

    private sealed class LineComparer : IEqualityComparer<LegajoExpectedDocument>
    {
        public static readonly LineComparer Instance = new();

        public bool Equals(LegajoExpectedDocument? x, LegajoExpectedDocument? y)
            => x is not null && y is not null
            && x.DocumentType == y.DocumentType && x.Required == y.Required && x.MaxAgeMonths == y.MaxAgeMonths
            && x.Alternatives.SequenceEqual(y.Alternatives);

        public int GetHashCode(LegajoExpectedDocument obj) => obj.DocumentType.GetHashCode(StringComparison.Ordinal);
    }
}
