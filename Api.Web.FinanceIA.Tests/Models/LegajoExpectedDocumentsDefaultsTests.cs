// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-31

using Api.Web.FinanceIA.Models.Financia;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Models;

/// <summary>
/// The CODE DEFAULTS of the expected-documents matrix (<see cref="LegajoExpectedDocuments"/>): what every tenant
/// without a <c>person.expected_documents</c> row — or without a given regime in it — reads. Same assertions as
/// when the matrix was the only one (LegajoCatalogueTests, M10 Phase 2a).
/// </summary>
public sealed class LegajoExpectedDocumentsDefaultsTests
{
    [Theory]
    [InlineData(FiscalRegimes.Monotributo)]
    [InlineData(FiscalRegimes.ResponsableInscripto)]
    [InlineData(FiscalRegimes.Exento)]
    [InlineData(FiscalRegimes.Desconocido)]
    [InlineData(null)]
    public void EveryRegime_RequiresTheAuthorization_AndKeepsTheFlowOptional(string? regime)
    {
        var lines = LegajoExpectedDocuments.For(regime);

        Assert.True(lines.Single(l => l.DocumentType == DocumentTypes.AutorizacionDatos).Required);
        Assert.False(lines.Single(l => l.DocumentType == DocumentTypes.FlujoProyectado).Required);
        Assert.All(lines, l => Assert.Contains(l.DocumentType, DocumentTypes.LegajoTypes));
    }

    [Fact]
    public void Monotributo_Matrix()
    {
        Assert.Equal(
            [DocumentTypes.AutorizacionDatos, DocumentTypes.Monotributo, DocumentTypes.CertificacionIngresos,
             DocumentTypes.ManifestacionBienes, DocumentTypes.DdjjIibb, DocumentTypes.BienesPersonales,
             DocumentTypes.FlujoProyectado],
            LegajoExpectedDocuments.For(FiscalRegimes.Monotributo).Select(l => l.DocumentType));
    }

    [Fact]
    public void ResponsableInscripto_Matrix()
    {
        Assert.Equal(
            [DocumentTypes.AutorizacionDatos, DocumentTypes.Ganancias, DocumentTypes.DdjjIva, DocumentTypes.DdjjIibb,
             DocumentTypes.BienesPersonales, DocumentTypes.ManifestacionBienes, DocumentTypes.CertificacionIngresos,
             DocumentTypes.FlujoProyectado],
            LegajoExpectedDocuments.For(FiscalRegimes.ResponsableInscripto).Select(l => l.DocumentType));
    }

    [Theory]
    [InlineData(FiscalRegimes.Exento)]
    [InlineData(FiscalRegimes.Desconocido)]
    public void ExentoAndDesconocido_Matrix(string regime)
    {
        Assert.Equal(
            [DocumentTypes.AutorizacionDatos, DocumentTypes.ManifestacionBienes, DocumentTypes.BienesPersonales,
             DocumentTypes.CertificacionIngresos, DocumentTypes.FlujoProyectado],
            LegajoExpectedDocuments.For(regime).Select(l => l.DocumentType));
    }

    [Fact]
    public void BienesPersonales_IsSatisfiedByReibpToo()
    {
        var line = LegajoExpectedDocuments.For(FiscalRegimes.Monotributo)
            .Single(l => l.DocumentType == DocumentTypes.BienesPersonales);

        Assert.True(line.IsSatisfiedBy(DocumentTypes.BienesPersonales));
        Assert.True(line.IsSatisfiedBy(DocumentTypes.Reibp));
        Assert.False(line.IsSatisfiedBy(DocumentTypes.Ganancias));
    }

    // ── The bucket vocabulary ───────────────────────────────────────────────────

    [Theory]
    [InlineData(FiscalRegimes.Monotributo, ExpectedDocumentRegimes.Monotributo)]
    [InlineData(FiscalRegimes.ResponsableInscripto, ExpectedDocumentRegimes.ResponsableInscripto)]
    [InlineData(FiscalRegimes.Exento, ExpectedDocumentRegimes.ExentoOrDesconocido)]
    [InlineData(FiscalRegimes.Desconocido, ExpectedDocumentRegimes.ExentoOrDesconocido)]
    [InlineData("junk", ExpectedDocumentRegimes.ExentoOrDesconocido)]
    [InlineData(null, ExpectedDocumentRegimes.ExentoOrDesconocido)]
    public void Defaults_ByBucket_AgreeWithFor(string? regime, string bucket)
    {
        Assert.Equal(bucket, ExpectedDocumentRegimes.Of(regime));
        Assert.Same(LegajoExpectedDocuments.Defaults[bucket], LegajoExpectedDocuments.For(regime));
    }

    [Fact]
    public void Defaults_CoverTheThreeBuckets_AndStateNoAgeCutOfTheirOwn()
    {
        Assert.Equal(
            ExpectedDocumentRegimes.All.OrderBy(k => k, StringComparer.Ordinal),
            LegajoExpectedDocuments.Defaults.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.All(LegajoExpectedDocuments.Defaults.Values.SelectMany(l => l), l => Assert.Null(l.MaxAgeMonths));
    }

    [Fact]
    public void ConfigurableTypes_AreTheLegajoTypes_ButOtro()
    {
        Assert.DoesNotContain(DocumentTypes.Otro, LegajoExpectedDocuments.ConfigurableTypes);
        Assert.Equal(DocumentTypes.LegajoTypes.Count - 1, LegajoExpectedDocuments.ConfigurableTypes.Count);
    }

    [Theory]
    [InlineData(DocumentTypes.AutorizacionDatos, LegajoDocumentAgeClasses.Timeless)]
    [InlineData(DocumentTypes.FlujoProyectado, LegajoDocumentAgeClasses.Timeless)]
    [InlineData(DocumentTypes.Ganancias, LegajoDocumentAgeClasses.Annual)]
    [InlineData(DocumentTypes.BienesPersonales, LegajoDocumentAgeClasses.Annual)]
    [InlineData(DocumentTypes.Reibp, LegajoDocumentAgeClasses.Annual)]
    [InlineData(DocumentTypes.ManifestacionBienes, LegajoDocumentAgeClasses.PointInTime)]
    [InlineData(DocumentTypes.DdjjIva, LegajoDocumentAgeClasses.PointInTime)]
    public void AgeClasses(string type, string ageClass)
        => Assert.Equal(ageClass, LegajoDocumentAgeClasses.Of(type));
}
