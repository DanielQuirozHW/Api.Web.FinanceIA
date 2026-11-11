// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-09

using System.Text;
using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// "Hallazgos del balance" in the risk section: the catalogue channel first with its note, the model channel after it
/// and only when it has rows, the Portal's empty notice, and an unreadable catalogue that is logged once and left out
/// without failing the export. Rendered end to end and read back as text.
/// </summary>
public sealed class PdfExportFindingsTests
{
    private const string CatalogueJson =
        """
        [
          {"level":"medium","code":"COBERTURA_INTERESES_INDETERMINABLE","title":"Cobertura de intereses indeterminable","description":"El estado de resultados no expone los gastos financieros por separado.","metric_key":null,"metric_value":null,"threshold_value":null,"metric_unit":null},
          {"level":"high","code":"ENDEUDAMIENTO_ELEVADO","title":"Endeudamiento elevado","description":"El pasivo representa el 41,8 % del activo, por encima del umbral configurado de 40,0 %.","metric_key":"liabilities_over_assets","metric_value":0.4184,"threshold_value":0.40,"metric_unit":"proportion"}
        ]
        """;

    private sealed class RecordingLogger : ILogger<PdfExportService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static async Task<string> RenderText(AnalysisResult result, IReadOnlyList<string> sections, RecordingLogger? logger = null)
    {
        var service = new PdfExportService(
            new CashFlowNarrativeService(),
            new CashFlowReconciliationService(),
            new AlertRatioReconciliationService(),
            (ILogger<PdfExportService>?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PdfExportService>.Instance);

        byte[] pdf = await service.GenerateAsync(
            result, PdfSyntheticReport.Analysis(), "tenant-synthetic", sections, CancellationToken.None, PdfSyntheticReport.Report());

        using var reader = new PdfReader(new MemoryStream(pdf));
        using var doc = new PdfDocument(reader);
        var sb = new StringBuilder();
        for (int i = 1; i <= doc.GetNumberOfPages(); i++)
            sb.Append(PdfTextExtractor.GetTextFromPage(doc.GetPage(i))).Append(' ');

        // Lines joined, so a wrapped note still reads as one sentence.
        return Regex.Replace(sb.ToString(), @"\s+", " ");
    }

    private static AnalysisResult Result(string? catalogue, string? riskAlerts = "keep")
    {
        AnalysisResult result = PdfSyntheticReport.Result();
        result.CatalogueAlerts = catalogue;
        if (riskAlerts != "keep")
            result.RiskAlerts = riskAlerts;
        return result;
    }

    private static readonly string[] Risk = ["risk"];

    [Fact]
    public async Task TheCatalogue_ComesFirst_WithItsNote_ThenTheModel_WithItsOwn()
    {
        string text = await RenderText(Result(CatalogueJson), Risk);

        int findings  = text.IndexOf("Hallazgos del balance", StringComparison.Ordinal);
        int catalogue = text.IndexOf("HALLAZGOS DE CATÁLOGO", StringComparison.Ordinal);
        int model     = text.IndexOf("OBSERVACIONES DEL MODELO", StringComparison.Ordinal);

        Assert.True(findings >= 0 && catalogue > findings && model > catalogue, $"order: {findings} {catalogue} {model}");
        Assert.Contains("Reglas deterministas: el mismo balance produce siempre los mismos hallazgos.", text);
        Assert.Contains("Lectura del modelo, no garantizada: un nuevo análisis del mismo balance puede redactarlas o codificarlas distinto.", text);
        Assert.DoesNotContain("Alertas activas", text);

        // Most severe first inside the catalogue block, its title as stored, the figures in the Portal's format.
        int high   = text.IndexOf("Endeudamiento elevado", catalogue, StringComparison.Ordinal);
        int medium = text.IndexOf("Cobertura de intereses indeterminable", catalogue, StringComparison.Ordinal);
        Assert.True(high > catalogue && medium > high && model > medium);
        Assert.Contains("Medido: 41,8 % · Umbral: 40,0 %", text);

        // The model's rows stay in their own block, after the catalogue's.
        Assert.True(text.IndexOf("Concentración de deuda en el corto plazo", model, StringComparison.Ordinal) > model);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task NoCatalogueRows_PrintThePortalsNotice(string? catalogue)
    {
        string text = await RenderText(Result(catalogue), Risk);

        Assert.Contains("HALLAZGOS DE CATÁLOGO", text);
        Assert.Contains("Sin hallazgos de catálogo", text);
        Assert.Contains("Ninguna regla del catálogo se activó en este análisis, o el análisis es anterior al catálogo.", text);
        Assert.Contains("OBSERVACIONES DEL MODELO", text);
    }

    [Fact]
    public async Task AnUnreadableCatalogue_IsLeftOut_LoggedOnce_AndTheExportGoesOn()
    {
        var logger = new RecordingLogger();

        string text = await RenderText(Result("""{"not":"an array"}"""), Risk, logger);

        Assert.DoesNotContain("HALLAZGOS DE CATÁLOGO", text);
        Assert.DoesNotContain("Sin hallazgos de catálogo", text);
        Assert.Contains("OBSERVACIONES DEL MODELO", text);

        // Page 1 and the risk section read the same parse: one Warning, ids and counts only.
        (LogLevel level, string message) = Assert.Single(logger.Entries, e => e.Message.Contains("catalogue alerts", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, level);
        Assert.DoesNotContain("an array", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[]")]
    [InlineData("[{\"level\":\"high\"}]")]
    public async Task NoModelRows_OmitTheModelBlock(string? riskAlerts)
    {
        string text = await RenderText(Result(CatalogueJson, riskAlerts), Risk);

        Assert.Contains("HALLAZGOS DE CATÁLOGO", text);
        Assert.DoesNotContain("OBSERVACIONES DEL MODELO", text);
    }

    [Fact]
    public async Task ThePageOneAttentionPoints_IncludeAHighCatalogueAlert()
    {
        // No risk section: page 1 alone reads the channel.
        string text = await RenderText(Result(CatalogueJson), ["footer"]);

        int attention = text.IndexOf("Puntos de atención", StringComparison.Ordinal);
        Assert.True(attention >= 0);
        Assert.True(text.IndexOf("Endeudamiento elevado", attention, StringComparison.Ordinal) > attention);
        Assert.DoesNotContain("Cobertura de intereses indeterminable", text);   // medium: not a candidate
    }
}
