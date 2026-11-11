// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Pdf;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfReport;

/// <summary>The report says what the Portal says: reliability, risk, status, sector, severity, BCRA, ratios.</summary>
public sealed class PdfLabelsTests
{
    [Theory]
    [InlineData(ScoreReliabilityLevels.Complete,    "Datos completos",  PdfTone.Good)]
    [InlineData(ScoreReliabilityLevels.WithCaveats, "Con notas",        PdfTone.Caution)]
    [InlineData(ScoreReliabilityLevels.Provisional, "Score provisorio", PdfTone.Bad)]
    [InlineData(ScoreReliabilityLevels.None,        "Sin score",        PdfTone.Muted)]
    [InlineData("WITH_CAVEATS",                     "Con notas",        PdfTone.Caution)]
    public void Reliability_UsesThePortalsWords(string level, string label, PdfTone tone)
    {
        (string actualLabel, PdfTone actualTone, _) = PdfLabels.Reliability(level);

        Assert.Equal(label, actualLabel);
        Assert.Equal(tone, actualTone);
    }

    [Theory]
    [InlineData("Low",      "Riesgo Bajo")]
    [InlineData("medium",   "Riesgo Medio")]
    [InlineData("High",     "Riesgo Alto")]
    [InlineData("CRITICAL", "Riesgo Crítico")]
    [InlineData(null,       "Sin clasificar")]
    public void RiskLevel_UsesThePortalsWords(string? raw, string expected)
    {
        Assert.Equal(expected, PdfLabels.RiskLevel(raw));
    }

    [Theory]
    [InlineData("PendingReview", "Pendiente de revisión")]
    [InlineData("PreApproved",   "Pre-aprobado")]
    [InlineData("Approved",      "Aprobado")]
    [InlineData("Rejected",      "Rechazado")]
    [InlineData("Queued",        "En cola")]
    public void AnalysisStatus_UsesThePortalsWords(string status, string expected)
    {
        Assert.Equal(expected, PdfLabels.AnalysisStatus(status));
    }

    [Theory]
    [InlineData("Construccion", "Construcción")]
    [InlineData("Agro",         "Agro")]
    [InlineData(null,           "General")]
    public void SectorProfile_IsAccented(string? profile, string expected)
    {
        Assert.Equal(expected, PdfLabels.SectorProfile(profile));
    }

    [Theory]
    [InlineData("critical", "Crítica", 4)]
    [InlineData("high",     "Alta",    3)]
    [InlineData("medium",   "Media",   2)]
    [InlineData("low",      "Baja",    1)]
    [InlineData("unknown",  "Baja",    1)]
    public void AlertSeverity_HasFourLevels(string level, string label, int rank)
    {
        (string actualLabel, int actualRank, _) = PdfLabels.AlertSeverity(level);

        Assert.Equal(label, actualLabel);
        Assert.Equal(rank, actualRank);
    }

    [Theory]
    [InlineData(1, "Normal")]
    [InlineData(2, "Con seguimiento especial")]
    [InlineData(5, "Irrecuperable")]
    [InlineData(9, "Desconocida")]
    public void BcraSituacion_UsesThePortalsWords(int situacion, string expected)
    {
        Assert.Equal(expected, PdfLabels.BcraSituacion(situacion));
    }

    [Theory]
    [InlineData(false, false, "Estados contables")]
    [InlineData(true,  false, "Balance de sumas y saldos")]
    [InlineData(false, true,  "Estados contables · corregido")]
    public void DocumentKind(bool isTrialBalance, bool corrected, string expected)
    {
        Assert.Equal(expected, PdfLabels.DocumentKind(isTrialBalance, corrected));
    }

    [Theory]
    [InlineData("debt_ratio",    0.42,  "0,42x")]     // endeudamiento in x, as on the screen — never %
    [InlineData("current_ratio", 1.5,   "1,50x")]
    [InlineData("roe",           12.4,  "12,40%")]
    [InlineData("net_margin",    -3.25, "−3,25%")]
    public void Ratios_UseThePortalsUnits(string key, double value, string expected)
    {
        Assert.Equal(expected, PdfExportService.FormatRatio(key, value));
    }

    [Fact]
    public void InterestCoverage_ReadsNoDeterminable_WhateverTheExclusionReason()
    {
        foreach (string? reason in new[] { null, RatioExclusionReasons.Unread, RatioExclusionReasons.NotMeaningful })
        {
            var entry = new RatioBreakdownEntry(50m, "Atencion", Excluded: true, ExclusionReason: reason);
            Assert.Equal("No determinable", PdfExportService.NotMeasurableLabel("interest_coverage", entry));
        }

        // Other ratios keep saying why.
        var unread = new RatioBreakdownEntry(50m, "Atencion", Excluded: true, ExclusionReason: RatioExclusionReasons.Unread);
        Assert.Equal("No se pudo leer", PdfExportService.NotMeasurableLabel("current_ratio", unread));
    }

    [Theory]
    [InlineData("202609", "09/2026")]
    [InlineData("202613", "202613")]
    [InlineData("2026",   "2026")]
    public void BcraPeriod_IsMonthSlashYear(string periodo, string expected)
    {
        Assert.Equal(expected, PdfExportService.FormatPeriodo(periodo));
    }

    [Theory]
    [InlineData("## Justificación del score\n\nEl score refleja…", "El score refleja…")]
    [InlineData("# Resumen ejecutivo\nTexto.",                    "Texto.")]
    [InlineData("Texto sin título.\n## Más adelante",             "Texto sin título.\n## Más adelante")]
    [InlineData("#hashtag no es un título",                       "#hashtag no es un título")]
    public void ALeadingMarkdownHeading_IsDropped(string raw, string expected)
    {
        Assert.Equal(expected, PdfExportService.StripLeadingMarkdownHeading(raw));
    }
}

/// <summary>A risk alert is read by its title, never its raw CODE_IN_CAPS.</summary>
public sealed class AlertTitlesTests
{
    [Theory]
    [InlineData("INFLATION_ADJUSTMENT_RT6", "Título del modelo", "Ajuste por inflación (RT 6)")]   // curated wins
    [InlineData("FLUJO_NO_CONCILIA",        null,                "El estado de flujo de efectivo no concilia")]
    [InlineData("VENTAS_EN_CAIDA",          "Caída de ventas",   "Caída de ventas")]                // the model's title
    [InlineData("VENTAS_EN_CAIDA",          "  ",                "Ventas en caída")]                // humanised code, accented
    [InlineData("SITUACION_BCRA_CRITICA",   null,                "Situación bcra crítica")]
    [InlineData("PERDIDA_EN_EL_ULTIMO_EJERCICIO", null,          "Pérdida en el último ejercicio")]
    [InlineData("EMISION_DE_DEUDA",         null,                "Emisión de deuda")]
    [InlineData("OBLIGACIONES_VENCIDAS",    null,                "Obligaciones vencidas")]          // -ciones takes no accent
    [InlineData("LIQ-01",                   null,                "Liq 01")]
    [InlineData(null,                       null,                null)]
    public void Resolve(string? code, string? title, string? expected)
    {
        Assert.Equal(expected, AlertTitles.Resolve(code, title));
    }

    [Theory]
    [InlineData("ENDEUDAMIENTO_ELEVADO",    "Título del modelo", "Nombre del catálogo")]   // catalogue beats curated and model
    [InlineData("endeudamiento_elevado",    null,                "Nombre del catálogo")]   // codes match ignoring case
    [InlineData("INFLATION_ADJUSTMENT_RT6", "Título del modelo", "Ajuste por inflación (RT 6)")]
    [InlineData("DEUDA_CORTO_PLAZO",        "Título del modelo", "Título del modelo")]
    [InlineData("SIN_NOMBRE",               null,                "Sin nombre")]             // a blank catalogue name is skipped
    public void Resolve_PrefersTheTenantsCatalogueName(string code, string? title, string expected)
    {
        var catalogue = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ENDEUDAMIENTO_ELEVADO"] = "Nombre del catálogo",
            ["SIN_NOMBRE"]            = " ",
        };

        Assert.Equal(expected, AlertTitles.Resolve(code, title, catalogue));
    }
}
