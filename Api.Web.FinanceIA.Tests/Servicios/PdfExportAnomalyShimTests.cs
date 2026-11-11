// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-13

using Api.Web.FinanceIA.Servicios;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Covers the render-time compatibility shim <see cref="PdfExportService.ReformatReconciliationAnomaly"/>
/// that reformats the two decimal figures inside cash-flow reconciliation anomalies persisted BEFORE
/// the es-AR source fix in AnalysisController. The shim is anchored to the exact literal prefixes our
/// own code emits and touches only the two invariant decimals that follow them — it must never touch
/// an already-es-AR figure, an unrelated anomaly, or any other number.
/// </summary>
public sealed class PdfExportAnomalyShimTests
{
    [Fact]
    public void Reformat_ExactPersistedSectionsAnomaly_ReformatsBothFigures()
    {
        // The real Biotrust string as persisted (invariant F2, no separators).
        const string input =
            "El estado de flujo de efectivo no concilia: suma de secciones 423875995.69 " +
            "vs variación informada 804632266.63. Las cifras se muestran tal como fueron extraídas.";

        const string expected =
            "El estado de flujo de efectivo no concilia: suma de secciones 423.875.995,69 " +
            "vs variación informada 804.632.266,63. Las cifras se muestran tal como fueron extraídas.";

        Assert.Equal(expected, PdfExportService.ReformatReconciliationAnomaly(input));
    }

    [Fact]
    public void Reformat_ContinuityAnomaly_ReformatsBothFigures()
    {
        const string input =
            "El estado de flujo de efectivo no concilia: efectivo inicial más variación 1000000.00 " +
            "vs efectivo final informado 1500000.50. Las cifras se muestran tal como fueron extraídas.";

        const string expected =
            "El estado de flujo de efectivo no concilia: efectivo inicial más variación 1.000.000,00 " +
            "vs efectivo final informado 1.500.000,50. Las cifras se muestran tal como fueron extraídas.";

        Assert.Equal(expected, PdfExportService.ReformatReconciliationAnomaly(input));
    }

    [Fact]
    public void Reformat_NegativeFigures_KeepsSignAndReformats()
    {
        const string input =
            "El estado de flujo de efectivo no concilia: suma de secciones -50000.25 " +
            "vs variación informada 12345.00. Las cifras se muestran tal como fueron extraídas.";

        const string expected =
            "El estado de flujo de efectivo no concilia: suma de secciones \u221250.000,25 " +
            "vs variación informada 12.345,00. Las cifras se muestran tal como fueron extraídas.";

        Assert.Equal(expected, PdfExportService.ReformatReconciliationAnomaly(input));
    }

    [Fact]
    public void Reformat_AlreadyEsArFormatted_IsUnchanged()
    {
        // A figure already carrying es-AR grouping/decimal-comma does not match the invariant token,
        // so a re-exported (already-fixed) anomaly is passed through verbatim — never double-formatted.
        const string input =
            "El estado de flujo de efectivo no concilia: suma de secciones 423.875.995,69 " +
            "vs variación informada 804.632.266,63. Las cifras se muestran tal como fueron extraídas.";

        Assert.Equal(input, PdfExportService.ReformatReconciliationAnomaly(input));
    }

    [Fact]
    public void Reformat_UnrelatedAnomaly_IsUnchanged()
    {
        const string input = "Patrimonio neto negativo: la empresa presenta insolvencia técnica.";

        Assert.Equal(input, PdfExportService.ReformatReconciliationAnomaly(input));
    }

    [Fact]
    public void Reformat_AnomalyWithOtherNumbers_IsUntouched()
    {
        // Not our reconciliation prefix — the shim must not touch these numbers (nor an analysis id).
        const string input =
            "Se detectaron 3 ajustes contables por 1234567.89 en el ejercicio 2025 (ID 8F3A2C10).";

        Assert.Equal(input, PdfExportService.ReformatReconciliationAnomaly(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Reformat_EmptyOrWhitespace_IsReturnedAsIs(string input)
    {
        Assert.Equal(input, PdfExportService.ReformatReconciliationAnomaly(input));
    }
}
