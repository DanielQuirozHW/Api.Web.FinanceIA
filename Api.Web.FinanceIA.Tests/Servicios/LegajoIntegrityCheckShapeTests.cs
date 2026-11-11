// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-19

using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The Portal #345 additions to <see cref="LegajoIntegrityCheck"/>: <c>figureKind</c> on every check that
/// carries figures, and <c>subjectParts</c>, the structured form of <c>subject</c>. Both additive: the
/// subject string is unchanged and a null is omitted from the JSON.
/// </summary>
public sealed class LegajoIntegrityCheckShapeTests
{
    private static readonly LegajoIntegrityService Sut = new();
    private static readonly DateOnly PeriodEnd2025 = new(2025, 12, 31);

    private static LegajoIntegrityReport Evaluate(string documentType, Action<JsonObject>? mutate = null)
        => Sut.Evaluate(documentType, LegajoExtractionFixtures.NormalizedClean(documentType, mutate), PeriodEnd2025);

    [Theory]
    [InlineData(DocumentTypes.BienesPersonales)]
    [InlineData(DocumentTypes.ManifestacionBienes)]
    [InlineData(DocumentTypes.Ganancias)]
    [InlineData(DocumentTypes.DdjjIva)]
    [InlineData(DocumentTypes.DdjjIibb)]
    [InlineData(DocumentTypes.CertificacionIngresos)]
    [InlineData(DocumentTypes.Monotributo)]
    public void EveryCheckWithFigures_HasAFigureKind_AndEveryRuleWithoutThemHasNone(string documentType)
    {
        var report = Evaluate(documentType);

        Assert.NotEmpty(report.Checks);
        foreach (var check in report.Checks)
        {
            if (check.Expected is null && check.Actual is null)
                Assert.Null(check.FigureKind);
            else
                Assert.Contains(check.FigureKind, LegajoFigureKinds.Known);
        }
    }

    [Theory]
    [InlineData(DocumentTypes.BienesPersonales, LegajoIntegrityService.BpFiscalYearPeriodCheck, LegajoFigureKinds.Year)]
    [InlineData(DocumentTypes.Ganancias, LegajoIntegrityService.GanFiscalYearPeriodCheck, LegajoFigureKinds.Year)]
    [InlineData(DocumentTypes.DdjjIva, LegajoIntegrityService.IvaPeriodCheck, LegajoFigureKinds.Month)]
    [InlineData(DocumentTypes.DdjjIibb, LegajoIntegrityService.IibbPeriodCheck, LegajoFigureKinds.Month)]
    [InlineData(DocumentTypes.CertificacionIngresos, LegajoIntegrityService.CiPeriodCheck, LegajoFigureKinds.Month)]
    [InlineData(DocumentTypes.Monotributo, LegajoIntegrityService.MonoPeriodCheck, LegajoFigureKinds.Month)]
    [InlineData(DocumentTypes.BienesPersonales, LegajoIntegrityService.BpTotalBienesCheck, LegajoFigureKinds.Money)]
    [InlineData(DocumentTypes.ManifestacionBienes, LegajoIntegrityService.MbPatrimonioNetoCheck, LegajoFigureKinds.Money)]
    public void FigureKind_PerCheckFamily(string documentType, string check, string figureKind)
    {
        Assert.Equal(figureKind, Assert.Single(Evaluate(documentType).Checks, c => c.Name == check).FigureKind);
    }

    [Fact]
    public void FlagRules_CarryNoFigureKind()
    {
        var report = Evaluate(DocumentTypes.BienesPersonales);

        Assert.Null(Assert.Single(report.Checks, c => c.Name == LegajoIntegrityService.BpOwnershipPctCheck).FigureKind);
    }

    [Fact]
    public void SubjectParts_BienesPersonales_RubroAndLocation()
    {
        var check = Assert.Single(Evaluate(DocumentTypes.BienesPersonales).Checks,
                                  c => c.Name == LegajoIntegrityService.BpRubroItemsCheck && c.Subject == "Depositos/Exterior");

        Assert.Equal(new Dictionary<string, string> { ["rubro"] = "Depositos", ["location"] = "Exterior" }, check.SubjectParts);
    }

    [Fact]
    public void SubjectParts_Manifestacion_SectionAndAnnex()
    {
        var report = Evaluate(DocumentTypes.ManifestacionBienes);

        Assert.Equal(new Dictionary<string, string> { ["section"] = "PasivoCorriente" },
                     Assert.Single(report.Checks, c => c.Name == LegajoIntegrityService.MbSectionLinesCheck
                                                    && c.Subject == "PasivoCorriente").SubjectParts);
        Assert.Equal(new Dictionary<string, string> { ["annexRef"] = "A" },
                     Assert.Single(report.Checks, c => c.Name == LegajoIntegrityService.MbAnnexItemsCheck && c.Subject == "A").SubjectParts);
    }

    [Fact]
    public void SubjectParts_Iva_PeriodAlicuotaAndRectificativa()
    {
        var report = Evaluate(DocumentTypes.DdjjIva, input =>
        {
            foreach (var row in input["periods"]!.AsArray().Concat(input["alicuotaLines"]!.AsArray()))
            {
                if (row!["period"]!.GetValue<string>() == "2025-12")
                    row["rectificativaNumber"] = 2;
            }
        });

        Assert.Equal(new Dictionary<string, string> { ["period"] = "2025-11", ["alicuotaPct"] = "10.5" },
                     Assert.Single(report.Checks, c => c.Name == LegajoIntegrityService.IvaAlicuotaLineCheck
                                                    && c.Subject == "2025-11/10.5").SubjectParts);
        Assert.Equal(new Dictionary<string, string> { ["period"] = "2025-12", ["rectificativa"] = "2" },
                     Assert.Single(report.Checks, c => c.Name == LegajoIntegrityService.IvaSaldoTecnicoCheck
                                                    && c.Subject == "2025-12/R2").SubjectParts);
    }

    [Fact]
    public void SubjectParts_Iibb_PeriodAndJurisdiction_AndAnnual()
    {
        var monthly = Evaluate(DocumentTypes.DdjjIibb);
        Assert.Equal(new Dictionary<string, string> { ["period"] = "2025-11", ["jurisdiction"] = "CABA" },
                     Assert.Single(monthly.Checks, c => c.Name == LegajoIntegrityService.IibbRowTaxCheck
                                                     && c.Subject == "2025-11/CABA").SubjectParts);

        var annual = Evaluate(DocumentTypes.DdjjIibb, input =>
        {
            input["header"]!["fiscalYear"] = LegajoExtractionFixtures.F(2025);
            foreach (var row in input["periods"]!.AsArray().Concat(input["rows"]!.AsArray()))
                row!["period"] = null;
        });
        Assert.Contains(annual.Checks, c => c.Name == LegajoIntegrityService.IibbTotalTaxCheck
                                         && c.SubjectParts is { } parts && parts["period"] == "anual");
    }

    [Fact]
    public void ChecksWithoutASubject_HaveNoSubjectParts_AndTheJsonOmitsThem()
    {
        var check = Assert.Single(Evaluate(DocumentTypes.BienesPersonales).Checks, c => c.Name == LegajoIntegrityService.BpTotalBienesCheck);

        Assert.Null(check.Subject);
        Assert.Null(check.SubjectParts);
        var json = JsonSerializer.Serialize(check);
        Assert.DoesNotContain("subjectParts", json);
        Assert.Contains("\"figureKind\":\"money\"", json);
    }

    [Fact]
    public void SubjectParts_Serialize_AsAnObject()
    {
        var check = Assert.Single(Evaluate(DocumentTypes.BienesPersonales).Checks,
                                  c => c.Name == LegajoIntegrityService.BpRubroItemsCheck && c.Subject == "Inmuebles/Pais");

        var json = JsonNode.Parse(JsonSerializer.Serialize(check))!;

        Assert.Equal("Inmuebles", json["subjectParts"]!["rubro"]!.GetValue<string>());
        Assert.Equal("Inmuebles/Pais", json["subject"]!.GetValue<string>());
    }
}
