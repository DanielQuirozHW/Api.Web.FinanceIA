// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-29

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using Xunit;

namespace Api.Web.FinanceIA.Tests.PersonAnalysis;

/// <summary>
/// The es-AR labels ported from the Portal: a few pinned texts, every engine id has a label, and an unknown
/// id prints as itself rather than throwing.
/// </summary>
public sealed class PersonLabelsTests
{
    [Theory]
    [InlineData(PersonFilterOutcomes.Rechazo, null, "Rechazo")]
    [InlineData(PersonFilterOutcomes.Alerta, PersonSeverities.Fuerte, "Alerta fuerte")]
    [InlineData(PersonFilterOutcomes.Alerta, PersonSeverities.Normal, "Alerta")]
    [InlineData(PersonFilterOutcomes.Alerta, null, "Alerta")]
    [InlineData(PersonFilterOutcomes.Ok, null, "Ok")]
    [InlineData(PersonFilterOutcomes.SinDatos, null, "Sin datos")]
    [InlineData("", null, "Sin datos")]
    [InlineData("Nuevo", null, "Nuevo")]
    public void FilterOutcome_ReadsAsThePortalChip(string outcome, string? severity, string expected)
        => Assert.Equal(expected, PersonLabels.FilterOutcome(outcome, severity));

    [Fact]
    public void PinnedTexts_MatchThePortal()
    {
        Assert.Equal("CUIT activo en ARCA", PersonLabels.Filter("arca_cuit_activo"));
        Assert.Equal("Cobertura del servicio de deuda", PersonLabels.Indicator("cobertura_servicio_deuda"));
        Assert.Equal("Rechazo sugerido", PersonLabels.Suggestion(PersonSuggestions.RechazoSugerido));
        Assert.Equal("Para decisión del analista", PersonLabels.Suggestion(PersonSuggestions.ParaDecisionDelAnalista));
        Assert.Equal("DDJJ Bienes Personales", PersonLabels.DocumentType(DocumentTypes.BienesPersonales));
        Assert.Equal("Monotributo · Cat. B", PersonLabels.FiscalRegime(FiscalRegimes.Monotributo, "b"));
        Assert.Equal("Responsable inscripto", PersonLabels.FiscalRegime(FiscalRegimes.ResponsableInscripto, "B"));
        Assert.Equal("Umbral provisorio", PersonLabels.ProvisionalLabel);
        Assert.Equal("Monto consumido (F.711)", PersonLabels.Input("montoConsumido"));
    }

    [Fact]
    public void UnknownIds_PrintAsThemselves()
    {
        Assert.Equal("filtro_nuevo", PersonLabels.Filter("filtro_nuevo"));
        Assert.Equal("indicador_nuevo", PersonLabels.Indicator("indicador_nuevo"));
        Assert.Equal("TipoNuevo", PersonLabels.DocumentType("TipoNuevo"));
        Assert.Equal("claveNueva", PersonLabels.Input("claveNueva"));
        Assert.Equal("Violeta", PersonLabels.Band("Violeta"));
        Assert.Equal("codigo_nuevo", PersonLabels.CannotConclude("codigo_nuevo", "  "));
        Assert.Equal(string.Empty, PersonLabels.Filter(null));
        Assert.Null(PersonLabels.Band(null));
    }

    [Fact]
    public void EveryEngineIdHasALabel()
    {
        var result = PersonEvaluationService.Evaluate(PersonEvaluationFixtures.Empty());

        Assert.All(result.Filters, f => Assert.True(PersonLabels.FilterLabels.ContainsKey(f.Id), f.Id));
        Assert.All(result.Indicators, i => Assert.True(PersonLabels.IndicatorLabels.ContainsKey(i.Id), i.Id));
        // The engine always stores a statement's text; the Portal's fallback map is only for a blank one and
        // does not list every code (sin_ventas_12_meses has none), so the stored text is what prints.
        Assert.All(result.Coverage.CannotConclude, c => Assert.Equal(c.Text, PersonLabels.CannotConclude(c.Code, c.Text)));
        Assert.All(result.Coverage.Items, i => Assert.True(PersonLabels.DocumentTypeLabels.ContainsKey(i.DocumentType), i.DocumentType));
    }
}
