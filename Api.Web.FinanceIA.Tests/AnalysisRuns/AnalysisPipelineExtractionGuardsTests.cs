// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using System.Text.Json;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Xunit;
using static Api.Web.FinanceIA.Tests.AnalysisRuns.AnalysisRunTestData;

namespace Api.Web.FinanceIA.Tests.AnalysisRuns;

/// <summary>
/// Extraction guards, phase 1, in the pipeline: a failed (paid) extraction leaves one ApiCallLogs
/// row, and a document stating another fiscal year than the requested one gets an es-AR anomaly.
/// Neither moves the score.
/// </summary>
public sealed class AnalysisPipelineExtractionGuardsTests
{
    // ── Failed extraction → one ApiCallLogs row ──────────────────────────────────

    [Fact]
    public async Task A_failed_extraction_with_usage_writes_one_failure_row_with_its_tokens_cost_and_document()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        var failure = new ClaudeApiException("Claude extraction hit max_tokens (16000) before completing the JSON (model claude-sonnet-4-6).")
        {
            InputTokens  = 1000,
            OutputTokens = 500,
            Model        = "claude-sonnet-4-6",
        };
        h.Claude.AnalyzeThrows = failure;

        var outcome = await h.Build().RunAsync(Run(doc), CancellationToken.None);

        Assert.Equal(AnalysisRunStatus.ExtractionFailed, outcome.Status);
        var row = Assert.Single(h.ApiCalls.Rows);
        Assert.Equal(ApiCallLogOperations.Extraction, row.Operation);
        Assert.Equal("Claude", row.Service);
        Assert.False(row.IsSuccess);
        Assert.Equal("claude-sonnet-4-6", row.Model);
        Assert.Equal(1000, row.InputTokens);
        Assert.Equal(500, row.OutputTokens);
        Assert.Equal(0.0105m, row.EstimatedCostUsd);   // 1,000 × $3/M + 500 × $15/M
        Assert.Equal(200, row.ResponseStatus);         // usage came back: the API answered
        Assert.Null(row.AnalysisId);                   // no analysis was created
        Assert.Equal(doc.DocumentId, row.DocumentId);
        // The internal diagnostic, verbatim — never the client-safe text the exception's Message carries.
        Assert.Equal(failure.DiagnosticMessage, row.ErrorMessage);
        Assert.NotEqual(failure.Message, row.ErrorMessage);
        Assert.Empty(h.Analyses.Created);
    }

    [Fact]
    public async Task A_failed_extraction_without_usage_writes_a_failure_row_with_zero_tokens_and_no_cost()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.Claude.AnalyzeThrows = new ClaudeApiException("Claude API error: HTTP 529.", 529) { Model = "claude-sonnet-4-6" };

        await h.Build().RunAsync(Run(doc), CancellationToken.None);

        var row = Assert.Single(h.ApiCalls.Rows);
        Assert.False(row.IsSuccess);
        Assert.Equal(529, row.ResponseStatus);
        Assert.Equal(0, row.InputTokens);
        Assert.Equal(0, row.OutputTokens);
        Assert.Null(row.EstimatedCostUsd);
        Assert.Equal(ApiCallLogOperations.Extraction, row.Operation);
    }

    [Fact]
    public async Task A_successful_extraction_still_writes_only_its_success_row()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);

        await h.Build().RunAsync(Run(doc), CancellationToken.None);

        var row = Assert.Single(h.ApiCalls.Rows, r => r.Operation == ApiCallLogOperations.Extraction);
        Assert.True(row.IsSuccess);
    }

    // ── Fiscal year read vs requested → anomaly ──────────────────────────────────

    private static async Task<List<string>> AnomaliesAfterRunAsync(int? detectedFiscalYear)
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        var balanceJson = detectedFiscalYear is { } year
            ? $$"""{"assets": 1000, "fiscal_year_detected": {{year}}}"""
            : """{"assets": 1000, "fiscal_year_detected": null}""";
        h.Claude.Result = ClaudeResult() with
        {
            BalanceJson   = balanceJson,
            AnomaliesJson = """["Anomalía del modelo."]""",
        };

        // Run() requests fiscal year 2024.
        await h.Build().RunAsync(Run(doc), CancellationToken.None);

        var result = Assert.Single(h.Results.Created);
        return JsonSerializer.Deserialize<List<string>>(result.Anomalies!)!;
    }

    [Fact]
    public async Task A_document_of_another_fiscal_year_appends_the_es_AR_anomaly_after_the_models_own()
    {
        var anomalies = await AnomaliesAfterRunAsync(2023);

        Assert.Equal(
            [
                "Anomalía del modelo.",
                "El ejercicio detectado en el documento (2023) no coincide con el ejercicio del análisis (2024).",
            ],
            anomalies);
    }

    [Fact]
    public async Task A_document_one_year_ahead_gets_the_closing_year_hint()
    {
        var anomalies = await AnomaliesAfterRunAsync(2025);

        Assert.Contains(
            "El ejercicio detectado en el documento (2025) no coincide con el ejercicio del análisis (2024). " +
            "Si el ejercicio no cierra en diciembre, el ejercicio del análisis se identifica por el año de cierre.",
            anomalies);
    }

    [Theory]
    [InlineData(2024)]   // match
    [InlineData(null)]   // not detected: nothing to compare, no anomaly
    public async Task A_matching_or_undetected_fiscal_year_adds_no_anomaly(int? detected)
    {
        var anomalies = await AnomaliesAfterRunAsync(detected);

        Assert.Equal(["Anomalía del modelo."], anomalies);
    }
}
