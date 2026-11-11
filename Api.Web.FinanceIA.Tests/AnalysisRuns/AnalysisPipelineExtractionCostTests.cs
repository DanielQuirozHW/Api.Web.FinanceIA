// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-01

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Xunit;
using static Api.Web.FinanceIA.Tests.AnalysisRuns.AnalysisRunTestData;

namespace Api.Web.FinanceIA.Tests.AnalysisRuns;

/// <summary>
/// The extraction's ApiCallLogs row is priced by the model that actually ran (ModelUsed), not the
/// Sonnet 4.6 rate the pipeline used to hardcode, and the Analysis row records that model.
/// </summary>
public sealed class AnalysisPipelineExtractionCostTests
{
    [Theory]
    // 1,000 in × $3/M + 500 out × $15/M — the figure the former inline formula produced.
    [InlineData("claude-sonnet-4-6", 0.0105)]
    // 1,000 in × $2/M + 500 out × $10/M.
    [InlineData("claude-sonnet-5-5", 0.007)]
    public async Task Extraction_row_carries_the_cost_and_model_of_ModelUsed(string model, double expectedCost)
    {
        var h   = new PipelineHarness();
        h.Claude.Result = ClaudeResult() with { ModelUsed = model };
        var doc = Document(TenantA);
        h.Store.Add(doc);

        var outcome = await h.Build().RunAsync(Run(doc), CancellationToken.None);

        Assert.Equal(AnalysisRunStatus.Completed, outcome.Status);

        // The row is written fire-and-forget; with the synchronous fake it has landed by now.
        var row = Assert.Single(h.ApiCalls.Rows, r => r.Operation == ApiCallLogOperations.Extraction);
        Assert.Equal(model, row.Model);
        Assert.Equal((decimal)expectedCost, row.EstimatedCostUsd);

        var analysis = Assert.Single(h.Analyses.Created);
        Assert.Equal(model, analysis.AiModel);
        // Fixed by CK_Analyses_AiTemperature until a DDL lets it record "not sent".
        Assert.Equal(0.00m, analysis.AiTemperature);
    }

    [Fact]
    public async Task A_scan_orientation_call_gets_its_own_row_priced_by_its_own_model()
    {
        var h = new PipelineHarness();
        h.Claude.Result = ClaudeResult() with
        {
            ModelUsed            = "claude-sonnet-4-6",
            OrientationDetection = new ScanOrientationUsage
            {
                Model = "claude-haiku-4-5", InputTokens = 2_000, OutputTokens = 100, DurationMs = 1_800,
                Succeeded = true, HttpStatus = 200, PagesAsked = 3, PagesAnswered = 3
            }
        };
        var doc = Document(TenantA);
        h.Store.Add(doc);

        var outcome = await h.Build().RunAsync(Run(doc), CancellationToken.None);

        Assert.Equal(AnalysisRunStatus.Completed, outcome.Status);
        var rows = h.ApiCalls.Rows.Where(r => r.Operation == ApiCallLogOperations.Extraction).ToList();
        Assert.Equal(2, rows.Count);

        // The extraction row is the Sonnet passes' alone.
        var extraction = Assert.Single(rows, r => r.Model == "claude-sonnet-4-6");
        Assert.Equal(ClaudeModelPricing.EstimateCostUsd("claude-sonnet-4-6", h.Claude.Result.InputTokens, h.Claude.Result.OutputTokens),
            extraction.EstimatedCostUsd);

        // 2,000 in × $1/M + 100 out × $5/M.
        var orientation = Assert.Single(rows, r => r.Model == "claude-haiku-4-5");
        Assert.Equal(0.0025m, orientation.EstimatedCostUsd);
        Assert.True(orientation.IsSuccess);
        Assert.Equal(200, orientation.ResponseStatus);
        Assert.Null(orientation.ErrorMessage);
        Assert.Equal(extraction.AnalysisId, orientation.AnalysisId);
        Assert.Equal("Claude", orientation.Service);
    }

    [Fact]
    public async Task A_failed_scan_orientation_call_is_logged_as_failed_with_its_code()
    {
        var h = new PipelineHarness();
        h.Claude.Result = ClaudeResult() with
        {
            OrientationDetection = new ScanOrientationUsage
            {
                Model = "claude-haiku-4-5", Succeeded = false, ErrorCode = "claude_timeout", PagesAsked = 2
            }
        };
        var doc = Document(TenantA);
        h.Store.Add(doc);

        await h.Build().RunAsync(Run(doc), CancellationToken.None);

        var orientation = Assert.Single(h.ApiCalls.Rows, r => r.Model == "claude-haiku-4-5");
        Assert.False(orientation.IsSuccess);
        Assert.Equal("claude_timeout", orientation.ErrorMessage);
        Assert.Null(orientation.ResponseStatus);
        Assert.Null(orientation.EstimatedCostUsd); // no response: unknown, not free
    }

    [Fact]
    public async Task A_scan_orientation_http_error_row_carries_the_code_then_the_api_reason()
    {
        var h = new PipelineHarness();
        h.Claude.Result = ClaudeResult() with
        {
            OrientationDetection = new ScanOrientationUsage
            {
                Model       = "claude-haiku-4-5",
                Succeeded   = false,
                HttpStatus  = 400,
                ErrorCode   = "claude_api_error",
                ErrorDetail = "HTTP 400 invalid_request_error: image too large [req_o1]",
                PagesAsked  = 2
            }
        };
        var doc = Document(TenantA);
        h.Store.Add(doc);

        await h.Build().RunAsync(Run(doc), CancellationToken.None);

        var orientation = Assert.Single(h.ApiCalls.Rows, r => r.Model == "claude-haiku-4-5");
        Assert.Equal("claude_api_error | HTTP 400 invalid_request_error: image too large [req_o1]", orientation.ErrorMessage);
    }

    [Fact]
    public async Task Without_a_scan_orientation_call_there_is_one_extraction_row()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);

        await h.Build().RunAsync(Run(doc), CancellationToken.None);

        Assert.Single(h.ApiCalls.Rows, r => r.Operation == ApiCallLogOperations.Extraction);
    }
}
