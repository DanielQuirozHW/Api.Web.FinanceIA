// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-04

using System.Net.Http;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Xunit;
using static Api.Web.FinanceIA.Tests.AnalysisRuns.AnalysisRunTestData;

namespace Api.Web.FinanceIA.Tests.AnalysisRuns;

/// <summary>
/// Which failed extractions spend the file's extraction budget, and that every one of them leaves its
/// ApiCallLogs row tied to the document. Our own failures (the provider's 5xx and 429, the network, our
/// key, an interrupted run, a fault after the extraction answered) are recorded without spending an
/// attempt; a timeout, a refusal and an answer that is not a balance still spend one, so an endless loop
/// stays impossible.
/// </summary>
public sealed class AnalysisPipelineFailureAccountingTests
{
    private const string Model = "claude-sonnet-4-6";

    /// <summary>A pipeline-only fault type, so the stored type name is unambiguous (and not excluded like InvalidOperationException).</summary>
    private sealed class PersistenceFaultException : Exception
    {
        public PersistenceFaultException() : base("synthetic persistence fault") { }
    }

    private static ClaudeApiException Failure(string kind) => kind switch
    {
        "500"        => new ClaudeApiException("Claude API error: HTTP 500.", 500) { Model = Model },
        "503"        => new ClaudeApiException("Claude API error: HTTP 503.", 503) { Model = Model },
        "network"    => new ClaudeApiException("Claude API network error.", null, new HttpRequestException()) { Model = Model },
        "429"        => new ClaudeApiException("Claude API rate limit exceeded.", 429) { Model = Model },
        "401"        => new ClaudeApiException("Claude API error: HTTP 401.", 401) { Model = Model },
        "403"        => new ClaudeApiException("Claude API error: HTTP 403.", 403) { Model = Model },
        "max_tokens" => new ClaudeApiException("hit max_tokens") { Model = Model, InputTokens = 10, OutputTokens = 20, FailureCode = ClaudeService.AnswerMaxTokens },
        "timeout"    => new ClaudeApiException("Claude API request timed out.", null, new TaskCanceledException()) { Model = Model },
        "refusal"    => new ClaudeApiException("declined") { Model = Model, InputTokens = 10, OutputTokens = 1, FailureCode = ClaudeService.AnswerRefusal },
        "schema"     => new ClaudeApiException("schema") { Model = Model, InputTokens = 10, OutputTokens = 20, FailureCode = ClaudeService.AnswerSchemaInvalid },
        "empty"      => new ClaudeApiException("empty") { Model = Model, InputTokens = 10, OutputTokens = 2, FailureCode = ClaudeService.AnswerEmpty },
        "unparseable" => new ClaudeApiException("no readable JSON", clientMessage: ClaudeApiException.UnparseableAnswerClientMessage)
                         { Model = Model, InputTokens = 10, OutputTokens = 20, FailureCode = ClaudeService.AnswerUnparseable },
        "400"        => new ClaudeApiException("Claude API error: HTTP 400.", 400) { Model = Model },
        "no_statements" => new ClaudeApiException("read no financial statements", clientMessage: ClaudeApiException.NoFinancialStatementsClientMessage)
                         { Model = Model, InputTokens = 10, OutputTokens = 20, FailureCode = ClaudeService.AnswerNoFinancialStatements,
                           ExtractionPassesRun = 1, KeptConfidence = 0.9m },
        _            => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    [Theory]
    [InlineData("500",        "claude_server_error",      false)]
    [InlineData("503",        "claude_server_error",      false)]
    [InlineData("network",    "claude_network_error",     false)]
    [InlineData("429",        "claude_rate_limited",      false)]
    [InlineData("401",        "claude_auth_error",        false)]
    [InlineData("403",        "claude_auth_error",        false)]
    [InlineData("max_tokens", "claude_max_tokens",        false)]
    [InlineData("timeout",    "claude_timeout",           true)]
    [InlineData("refusal",    "claude_refusal",           true)]
    [InlineData("schema",     "claude_unreadable_answer", true)]
    [InlineData("empty",      "claude_unreadable_answer", true)]
    [InlineData("unparseable", "claude_unparseable_answer", false)]
    [InlineData("400",        "claude_api_error",         true)]
    [InlineData("no_statements", "claude_no_financial_statements", false)]
    public async Task Each_model_failure_records_its_code_charges_only_when_it_should_and_writes_one_row(
        string kind, string expectedStored, bool expectedCounts)
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.Claude.AnalyzeThrows = Failure(kind);

        var outcome = await h.Build().RunAsync(Run(doc, isBackground: false), CancellationToken.None);

        Assert.Equal(AnalysisRunStatus.ExtractionFailed, outcome.Status);
        Assert.Equal("Failed", h.Store.StatusOf(doc.DocumentId));

        var recorded = Assert.Single(h.Documents.ExtractionFailures);
        Assert.Equal(expectedStored, recorded.Error);
        Assert.Equal(expectedCounts, recorded.CountsAsAttempt);

        var row = Assert.Single(h.ApiCalls.Rows);
        Assert.False(row.IsSuccess);
        Assert.Equal(doc.DocumentId, row.DocumentId);
        Assert.Null(row.AnalysisId);
        Assert.Equal("Claude", row.Service);
    }

    [Fact]
    public async Task An_interrupted_background_run_is_recorded_without_spending_an_attempt_and_leaves_its_row()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.Claude.AnalyzeThrows = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => h.Build().RunAsync(Run(doc, isBackground: true), CancellationToken.None));

        Assert.Equal("Failed", h.Store.StatusOf(doc.DocumentId));

        var recorded = Assert.Single(h.Documents.ExtractionFailures);
        Assert.Equal(nameof(OperationCanceledException), recorded.Error);
        Assert.False(recorded.CountsAsAttempt);
        Assert.Equal(AnalysisRunFailureCodes.Interrupted, AnalysisRunFailureCodes.FromStoredReason(recorded.Error));

        var row = Assert.Single(h.ApiCalls.Rows);
        Assert.False(row.IsSuccess);
        Assert.Equal(doc.DocumentId, row.DocumentId);
        Assert.Equal(AnalysisPipeline.InterruptedCallMessage, row.ErrorMessage);
        Assert.Null(row.EstimatedCostUsd);
    }

    [Fact]
    public async Task An_unexplained_fault_during_the_extraction_still_spends_an_attempt_and_leaves_its_row()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.Claude.AnalyzeThrows = new PersistenceFaultException();

        await Assert.ThrowsAsync<PersistenceFaultException>(
            () => h.Build().RunAsync(Run(doc, isBackground: false), CancellationToken.None));

        var recorded = Assert.Single(h.Documents.ExtractionFailures);
        Assert.Equal(nameof(PersistenceFaultException), recorded.Error);
        Assert.True(recorded.CountsAsAttempt);

        var row = Assert.Single(h.ApiCalls.Rows);
        Assert.False(row.IsSuccess);
        Assert.Equal(doc.DocumentId, row.DocumentId);
        Assert.Equal(AnalysisPipeline.PipelineFaultCallMessagePrefix + nameof(PersistenceFaultException), row.ErrorMessage);
    }

    /// <summary>
    /// The analysis row could not be written, so the success row (which needs it) was never logged: the
    /// document WAS read, so no attempt is spent, and the billed call still gets its row, by document.
    /// </summary>
    [Fact]
    public async Task A_fault_after_the_extraction_answered_spends_no_attempt_and_logs_the_billed_call_by_document()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.Analyses.CreateThrows = new PersistenceFaultException();

        await Assert.ThrowsAsync<PersistenceFaultException>(
            () => h.Build().RunAsync(Run(doc, isBackground: false), CancellationToken.None));

        Assert.Equal("Failed", h.Store.StatusOf(doc.DocumentId));
        var recorded = Assert.Single(h.Documents.ExtractionFailures);
        Assert.False(recorded.CountsAsAttempt);

        var row = Assert.Single(h.ApiCalls.Rows);
        Assert.True(row.IsSuccess);
        Assert.Equal(doc.DocumentId, row.DocumentId);
        Assert.Null(row.AnalysisId);
        Assert.Equal(1000, row.InputTokens);
        Assert.Equal(500, row.OutputTokens);
        Assert.NotNull(row.EstimatedCostUsd);
    }

    [Fact]
    public async Task A_fault_after_the_extraction_row_spends_no_attempt_and_writes_no_second_row()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.Results.CreateThrows = new PersistenceFaultException();

        await Assert.ThrowsAsync<PersistenceFaultException>(
            () => h.Build().RunAsync(Run(doc, isBackground: false), CancellationToken.None));

        Assert.False(Assert.Single(h.Documents.ExtractionFailures).CountsAsAttempt);

        // The extraction row was written with the analysis, before the fault; nothing is duplicated.
        var row = Assert.Single(h.ApiCalls.Rows);
        Assert.True(row.IsSuccess);
        Assert.NotNull(row.AnalysisId);
    }

    [Fact]
    public async Task A_throwing_log_service_does_not_change_the_outcome_of_a_failed_extraction()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.ApiCalls.Throws = new PersistenceFaultException();
        h.Claude.AnalyzeThrows = new ClaudeApiException("Claude API error: HTTP 503.", 503) { Model = Model };

        var outcome = await h.Build().RunAsync(Run(doc, isBackground: false), CancellationToken.None);

        Assert.Equal(AnalysisRunStatus.ExtractionFailed, outcome.Status);
        Assert.Equal("Failed", h.Store.StatusOf(doc.DocumentId));
        Assert.Equal("claude_server_error", Assert.Single(h.Documents.ExtractionFailures).Error);
        Assert.Single(h.Notifier.Failed);
    }

    [Fact]
    public async Task A_throwing_log_service_does_not_change_the_outcome_of_an_interrupted_run()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.ApiCalls.Throws = new PersistenceFaultException();
        h.Claude.AnalyzeThrows = new OperationCanceledException();

        // The original fault surfaces, not the logger's.
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => h.Build().RunAsync(Run(doc, isBackground: true), CancellationToken.None));

        Assert.Equal("Failed", h.Store.StatusOf(doc.DocumentId));
        Assert.False(Assert.Single(h.Documents.ExtractionFailures).CountsAsAttempt);
    }

    [Fact]
    public async Task A_throwing_log_service_does_not_change_a_completed_run()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.ApiCalls.Throws = new PersistenceFaultException();

        var outcome = await h.Build().RunAsync(Run(doc, isBackground: false), CancellationToken.None);

        Assert.Equal(AnalysisRunStatus.Completed, outcome.Status);
        Assert.Empty(h.Documents.ExtractionFailures);
    }

    // ── The stored code of each transport failure ───────────────────────────────

    [Fact]
    public void An_escalated_call_that_timed_out_keeps_the_timeout_code()
    {
        // ExecuteExtractionPassAsync re-wraps an escalated call's failure keeping status and inner exception.
        var escalated = new ClaudeApiException(
            "Claude API request timed out. (escalated call after max_tokens 16000)", null, new TaskCanceledException())
        {
            InputTokens  = 100,
            OutputTokens = 16000,
        };

        Assert.Equal(AnalysisRunFailureCodes.StoredClaudeTimeout, AnalysisPipeline.ExtractionFailureCode(escalated));
    }

    [Fact]
    public void Every_new_stored_code_is_still_a_model_error_on_GET_runs()
    {
        foreach (var code in new[]
                 {
                     AnalysisRunFailureCodes.StoredClaudeTimeout, AnalysisRunFailureCodes.StoredClaudeNetworkError,
                     AnalysisRunFailureCodes.StoredClaudeServerError, AnalysisRunFailureCodes.StoredClaudeAuthError,
                     AnalysisRunFailureCodes.StoredClaudeRefusal, AnalysisRunFailureCodes.StoredClaudeUnreadableAnswer,
                     AnalysisRunFailureCodes.StoredClaudeUnparseableAnswer,
                     AnalysisRunFailureCodes.StoredClaudeNoFinancialStatements,
                 })
        {
            Assert.Equal(AnalysisRunFailureCodes.ModelError, AnalysisRunFailureCodes.FromStoredReason(code));
        }
    }

    // ── A file with no financial statements ─────────────────────────────────────

    [Fact]
    public async Task A_file_with_no_financial_statements_creates_no_analysis_spends_no_attempt_and_logs_both_billed_calls()
    {
        var logger = new CapturingLogger();
        var h      = new PipelineHarness { Logger = logger };
        var doc    = Document(TenantA);
        h.Store.Add(doc);
        h.Claude.AnalyzeThrows = new ClaudeApiException(
            "Claude extraction read no financial statements: none of the core totals is present (passes 2, model claude-sonnet-4-6).",
            clientMessage: ClaudeApiException.NoFinancialStatementsClientMessage)
        {
            Model                = Model,
            InputTokens          = 3000,
            OutputTokens         = 400,
            FailureCode          = ClaudeService.AnswerNoFinancialStatements,
            ExtractionPassesRun  = 2,
            KeptConfidence       = 0.15m,
            OrientationDetection = new Api.Web.FinanceIA.Models.Financia.ScanOrientationUsage
            {
                Model = "claude-haiku-4-5", InputTokens = 500, OutputTokens = 50, Succeeded = true, HttpStatus = 200
            },
        };

        var outcome = await h.Build().RunAsync(Run(doc, isBackground: false), CancellationToken.None);

        Assert.Equal(AnalysisRunStatus.ExtractionFailed, outcome.Status);
        Assert.Equal(AnalysisRunFailureCodes.StoredClaudeNoFinancialStatements, outcome.FailureCode);
        Assert.Equal("Failed", h.Store.StatusOf(doc.DocumentId));
        Assert.Empty(h.Analyses.Created);

        var recorded = Assert.Single(h.Documents.ExtractionFailures);
        Assert.Equal(AnalysisRunFailureCodes.StoredClaudeNoFinancialStatements, recorded.Error);
        Assert.False(recorded.CountsAsAttempt);

        // The extraction row: both passes' tokens, priced by the extraction model.
        Assert.Equal(2, h.ApiCalls.Rows.Count);
        var extraction = Assert.Single(h.ApiCalls.Rows, r => r.Model == Model);
        Assert.False(extraction.IsSuccess);
        Assert.Equal(3000, extraction.InputTokens);
        Assert.Equal(400, extraction.OutputTokens);
        Assert.Equal(Api.Web.FinanceIA.Models.Financia.ClaudeModelPricing.EstimateCostUsd(Model, 3000, 400), extraction.EstimatedCostUsd);
        Assert.Equal(doc.DocumentId, extraction.DocumentId);
        Assert.Null(extraction.AnalysisId);
        Assert.Contains("no financial statements", extraction.ErrorMessage, StringComparison.Ordinal);

        // The scan-orientation call that ran before it: its own row, its own model, tied to the document.
        var detector = Assert.Single(h.ApiCalls.Rows, r => r.Model == "claude-haiku-4-5");
        Assert.Equal(500, detector.InputTokens);
        Assert.Equal(doc.DocumentId, detector.DocumentId);
        Assert.Null(detector.AnalysisId);

        Assert.Single(h.Notifier.Failed);
        Assert.Contains(h.Audit.Entries, e => e.Action == AnalysisRunFailureCodes.FailedAuditAction);

        // One Warning, no Error: the file had no balance in it, nothing of ours failed.
        var warning = Assert.Single(logger.Entries, e => e.Message.Contains("no financial statements", StringComparison.Ordinal));
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, warning.Level);
        Assert.Contains(TenantA, warning.Message, StringComparison.Ordinal);
        Assert.Contains("passes 2", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Error);
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<AnalysisPipeline>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
