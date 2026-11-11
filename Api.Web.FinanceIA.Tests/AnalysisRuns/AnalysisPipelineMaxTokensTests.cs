// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Microsoft.Extensions.Logging;
using Xunit;
using static Api.Web.FinanceIA.Tests.AnalysisRuns.AnalysisRunTestData;

namespace Api.Web.FinanceIA.Tests.AnalysisRuns;

/// <summary>
/// What the pipeline does with an extraction that stopped at the project's own <c>max_tokens</c>, against
/// every other model failure: its own reason code, NO charge against the file's extraction budget, the
/// diagnostic (never the client text) in ApiCallLogs, an Analysis.Failed audit row whose severity
/// CK_AuditLogs_Severity accepts — and, on success, the near-cap Warning.
/// </summary>
public sealed class AnalysisPipelineMaxTokensTests
{
    private const string AfterEscalation =
        "Claude extraction hit max_tokens (32000) after escalation before completing the JSON (model claude-sonnet-4-6).";

    private static ClaudeApiException MaxTokensFailure() => new(AfterEscalation)
    {
        // Both calls of the escalated pass: 16000 + 32000 out, the input sent twice.
        InputTokens  = 2000,
        OutputTokens = 48000,
        Model        = "claude-sonnet-4-6",
        FailureCode  = ClaudeService.AnswerMaxTokens,
    };

    private static ClaudeApiException TimeoutFailure() =>
        new("Claude API request timed out.", null, new TaskCanceledException()) { Model = "claude-sonnet-4-6" };

    // ── The budget ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_max_tokens_failure_records_its_own_code_without_spending_an_attempt()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.Claude.AnalyzeThrows = MaxTokensFailure();

        var outcome = await h.Build().RunAsync(Run(doc), CancellationToken.None);

        Assert.Equal(AnalysisRunStatus.ExtractionFailed, outcome.Status);
        Assert.Equal("Failed", h.Store.StatusOf(doc.DocumentId));
        var failure = Assert.Single(h.Documents.ExtractionFailures);
        Assert.Equal("claude_max_tokens", failure.Error);
        Assert.False(failure.CountsAsAttempt);
        // GET /api/analyses/runs still reports it as a model error, by the claude_ prefix.
        Assert.Equal(AnalysisRunFailureCodes.ModelError, AnalysisRunFailureCodes.FromStoredReason(failure.Error));
        Assert.Single(h.Notifier.Failed);
    }

    [Fact]
    public async Task A_timeout_records_claude_timeout_and_still_spends_an_attempt()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.Claude.AnalyzeThrows = TimeoutFailure();

        await h.Build().RunAsync(Run(doc), CancellationToken.None);

        var failure = Assert.Single(h.Documents.ExtractionFailures);
        Assert.Equal("claude_timeout", failure.Error);
        Assert.True(failure.CountsAsAttempt);
    }

    [Theory]
    [InlineData(ClaudeService.AnswerRefusal,         "claude_refusal")]
    [InlineData(ClaudeService.AnswerSchemaInvalid,   "claude_unreadable_answer")]
    [InlineData(ClaudeService.AnswerEmpty,           "claude_unreadable_answer")]
    [InlineData(ClaudeService.AnswerNoText,          "claude_api_error")]
    [InlineData(ClaudeService.AnswerEnvelopeInvalid, "claude_api_error")]
    [InlineData(null,                                "claude_api_error")]
    public async Task Every_other_unusable_answer_still_spends_an_attempt(string? failureCode, string expectedStored)
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.Claude.AnalyzeThrows = new ClaudeApiException("unusable") { FailureCode = failureCode };

        await h.Build().RunAsync(Run(doc), CancellationToken.None);

        var failure = Assert.Single(h.Documents.ExtractionFailures);
        Assert.Equal(expectedStored, failure.Error);
        Assert.True(failure.CountsAsAttempt);
    }

    [Fact]
    public async Task A_file_with_no_financial_statements_is_recorded_without_spending_an_attempt()
    {
        // The file comes from LUFE as it is and the budget is per file hash: counting it would lock it.
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.Claude.AnalyzeThrows = new ClaudeApiException("no financial statements")
        {
            FailureCode = ClaudeService.AnswerNoFinancialStatements
        };

        await h.Build().RunAsync(Run(doc), CancellationToken.None);

        var failure = Assert.Single(h.Documents.ExtractionFailures);
        Assert.Equal("claude_no_financial_statements", failure.Error);
        Assert.False(failure.CountsAsAttempt);
    }

    [Fact]
    public async Task A_rate_limit_no_longer_spends_an_attempt()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.Claude.AnalyzeThrows = new ClaudeApiException("Claude API rate limit exceeded.", 429);

        await h.Build().RunAsync(Run(doc), CancellationToken.None);

        var failure = Assert.Single(h.Documents.ExtractionFailures);
        Assert.Equal("claude_rate_limited", failure.Error);
        Assert.False(failure.CountsAsAttempt);
    }

    [Theory]
    [InlineData("claude_max_tokens", false)]
    [InlineData("claude_rate_limited", false)]
    [InlineData("claude_server_error", false)]
    [InlineData("claude_network_error", false)]
    [InlineData("claude_auth_error", false)]
    [InlineData("claude_unparseable_answer", false)]
    [InlineData("claude_no_financial_statements", false)]
    [InlineData("claude_api_error", true)]
    [InlineData("claude_timeout", true)]
    [InlineData("claude_refusal", true)]
    [InlineData("claude_unreadable_answer", true)]
    [InlineData("TaskCanceledException", true)]
    public void Only_our_own_failures_are_uncharged(string code, bool expected)
        => Assert.Equal(expected, AnalysisPipeline.ExtractionFailureCountsAsAttempt(code));

    // ── ApiCallLogs carries the diagnostic ──────────────────────────────────────

    [Fact]
    public async Task The_failure_row_carries_the_diagnostic_and_the_cost_of_both_calls_never_the_client_text()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        var failure = MaxTokensFailure();
        h.Claude.AnalyzeThrows = failure;

        await h.Build().RunAsync(Run(doc), CancellationToken.None);

        var row = Assert.Single(h.ApiCalls.Rows);
        Assert.False(row.IsSuccess);
        Assert.Equal(failure.DiagnosticMessage, row.ErrorMessage);
        Assert.Contains("hit max_tokens (32000) after escalation", row.ErrorMessage);
        Assert.NotEqual(failure.Message, row.ErrorMessage);
        Assert.Equal(2000, row.InputTokens);
        Assert.Equal(48000, row.OutputTokens);
        Assert.Equal(0.726m, row.EstimatedCostUsd);   // 2,000 × $3/M + 48,000 × $15/M
    }

    // ── The audit row is one the database accepts ───────────────────────────────

    [Fact]
    public async Task The_claude_failure_audit_row_is_a_Warning()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.Claude.AnalyzeThrows = MaxTokensFailure();

        await h.Build().RunAsync(Run(doc), CancellationToken.None);

        var audit = Assert.Single(h.Audit.Entries, e => e.Action == AnalysisRunFailureCodes.FailedAuditAction);
        Assert.Equal(AuditSeverities.Warning, audit.Severity);
        Assert.Contains(audit.Severity, AuditSeverities.All);
    }

    // ── The success path: one cost row over the summed tokens, and the near-cap Warning ──

    [Fact]
    public async Task An_escalated_pass_is_priced_on_the_tokens_of_both_calls_in_one_row()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        // What ClaudeService returns for a pass that escalated: both calls' tokens, summed.
        h.Claude.Result = ClaudeResult() with { InputTokens = 1000 + 1000, OutputTokens = 16000 + 20000 };

        await h.Build().RunAsync(Run(doc), CancellationToken.None);

        var row = Assert.Single(h.ApiCalls.Rows, r => r.Operation == ApiCallLogOperations.Extraction);
        Assert.True(row.IsSuccess);
        Assert.Equal(2000, row.InputTokens);
        Assert.Equal(36000, row.OutputTokens);
        Assert.Equal(0.546m, row.EstimatedCostUsd);   // 2,000 × $3/M + 36,000 × $15/M
    }

    [Theory]
    [InlineData(12000, 16000, true)]    // exactly 75 %
    [InlineData(15999, 16000, true)]
    [InlineData(11999, 16000, false)]   // 74.99 %
    [InlineData(24000, 32000, true)]    // an escalated answer, against ITS cap
    [InlineData(23999, 32000, false)]
    public async Task A_successful_pass_at_75_percent_of_its_cap_logs_one_structured_Warning(
        int outputTokens, int maxTokens, bool expectWarning)
    {
        var logger = new RecordingPipelineLogger();
        var h      = new PipelineHarness { Logger = logger };
        var doc    = Document(TenantA);
        h.Store.Add(doc);
        h.Claude.Result = ClaudeResult() with
        {
            ExtractionPasses = [new ExtractionPassUsage("claude-sonnet-4-6", maxTokens, outputTokens, maxTokens > 16000)]
        };

        await h.Build().RunAsync(Run(doc), CancellationToken.None);

        var nearCap = logger.Entries.Where(e => e.Template.StartsWith("Claude extraction near max_tokens", StringComparison.Ordinal)).ToList();
        if (!expectWarning)
        {
            Assert.Empty(nearCap);
            return;
        }

        var entry = Assert.Single(nearCap);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(TenantA, entry.Values["TenantId"]);
        Assert.Equal("claude-sonnet-4-6", entry.Values["Model"]);
        Assert.Equal(maxTokens, entry.Values["MaxTokens"]);
        Assert.Equal(outputTokens, entry.Values["OutputTokens"]);
        Assert.Equal(100d * outputTokens / maxTokens, (double)entry.Values["Pct"]!, 6);
    }

    [Fact]
    public void Each_near_cap_pass_gets_its_own_Warning_and_a_result_without_passes_none()
    {
        var logger = new RecordingPipelineLogger();
        var result = ClaudeResult() with
        {
            ExtractionPasses =
            [
                new ExtractionPassUsage("claude-sonnet-4-6", 16000, 13000, false),
                new ExtractionPassUsage("claude-sonnet-4-6", 16000, 4000, false),
                new ExtractionPassUsage("claude-sonnet-4-6", 32000, 30000, true),
            ]
        };

        AnalysisPipeline.LogNearCapPasses(logger, TenantA, result);
        AnalysisPipeline.LogNearCapPasses(logger, TenantA, ClaudeResult());

        Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Warning));
    }

    /// <summary>Records each entry's level, message template and structured values.</summary>
    private sealed class RecordingPipelineLogger : ILogger<AnalysisPipeline>
    {
        public List<(LogLevel Level, string Template, IReadOnlyDictionary<string, object?> Values)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values   = new Dictionary<string, object?>(StringComparer.Ordinal);
            var template = string.Empty;
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    if (key == "{OriginalFormat}")
                        template = value as string ?? string.Empty;
                    else
                        values[key] = value;
                }
            }

            lock (Entries) Entries.Add((logLevel, template, values));
        }
    }
}
