// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-09

using System.Net.Http;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Xunit;
using static Api.Web.FinanceIA.Tests.AnalysisRuns.AnalysisRunTestData;

namespace Api.Web.FinanceIA.Tests.AnalysisRuns;

/// <summary>
/// The one automatic retry of a background run: which failures earn it, how long it waits
/// (<see cref="AnalysisAutoRetryPolicy"/>), and what the pipeline leaves behind when it does — the
/// document still Processing, the failure recorded without an attempt, the failed call's ApiCallLogs row,
/// and no Failed audit and no notification yet. Everything else is today's path, byte for byte.
/// </summary>
public sealed class AnalysisAutoRetryTests
{
    private const string Model = "claude-sonnet-4-6";

    private static ClaudeApiException Failure(string kind) => kind switch
    {
        "500"     => new ClaudeApiException("Claude API error: HTTP 500.", 500) { Model = Model },
        "529"     => new ClaudeApiException("Claude API error: HTTP 529.", 529) { Model = Model },
        "network" => new ClaudeApiException("Claude API network error.", null, new HttpRequestException()) { Model = Model },
        "429"     => new ClaudeApiException("Claude API rate limit exceeded.", 429) { Model = Model, RetryAfterSeconds = 45 },
        "timeout" => new ClaudeApiException("Claude API request timed out.", null, new TaskCanceledException()) { Model = Model },
        "401"     => new ClaudeApiException("Claude API error: HTTP 401.", 401) { Model = Model },
        "400"     => new ClaudeApiException("Claude API error: HTTP 400.", 400) { Model = Model },
        "refusal" => new ClaudeApiException("declined") { Model = Model, FailureCode = ClaudeService.AnswerRefusal },
        _         => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    // ── Policy ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("claude_server_error",            true)]
    [InlineData("claude_network_error",           true)]
    [InlineData("claude_rate_limited",            true)]
    [InlineData("claude_timeout",                 false)]
    [InlineData("claude_auth_error",              false)]
    [InlineData("claude_api_error",               false)]
    [InlineData("claude_refusal",                 false)]
    [InlineData("claude_unreadable_answer",       false)]
    [InlineData("claude_unparseable_answer",      false)]
    [InlineData("claude_max_tokens",              false)]
    [InlineData("claude_no_financial_statements", false)]
    [InlineData("stale_processing",               false)]
    [InlineData("TaskCanceledException",          false)]
    public void Only_the_three_transient_codes_of_ours_are_eligible_on_a_first_background_run(string code, bool expected)
    {
        var run = Run(Document(TenantA));

        Assert.Equal(expected, AnalysisAutoRetryPolicy.IsEligible(run, code));
    }

    [Theory]
    [InlineData("claude_server_error")]
    [InlineData("claude_network_error")]
    [InlineData("claude_rate_limited")]
    public void Neither_a_synchronous_run_nor_the_retry_itself_is_ever_eligible(string code)
    {
        var doc = Document(TenantA);

        Assert.False(AnalysisAutoRetryPolicy.IsEligible(Run(doc, isBackground: false), code));
        Assert.False(AnalysisAutoRetryPolicy.IsEligible(Run(doc) with { IsAutoRetry = true }, code));
        Assert.False(AnalysisAutoRetryPolicy.IsEligible(Run(doc), null));
    }

    [Fact]
    public void Every_eligible_code_is_one_that_spends_no_attempt()
    {
        Assert.All(AnalysisAutoRetryPolicy.EligibleCodes,
            code => Assert.False(AnalysisPipeline.ExtractionFailureCountsAsAttempt(code)));
    }

    [Theory]
    [InlineData("claude_server_error",  null, 60)]
    [InlineData("claude_server_error",  30,   60)]    // a retry-after on anything but a 429 is ignored
    [InlineData("claude_network_error", null, 60)]
    [InlineData("claude_rate_limited",  null, 60)]
    [InlineData("claude_rate_limited",  0,    60)]
    [InlineData("claude_rate_limited",  -5,   60)]
    [InlineData("claude_rate_limited",  1,    1)]
    [InlineData("claude_rate_limited",  30,   30)]
    [InlineData("claude_rate_limited",  120,  120)]
    [InlineData("claude_rate_limited",  500,  120)]   // capped
    public void The_delay_is_60_seconds_or_a_429s_retry_after_capped_at_120(string code, int? retryAfter, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), AnalysisAutoRetryPolicy.DelayFor(code, retryAfter));
    }

    // ── Pipeline ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("500",     "claude_server_error")]
    [InlineData("529",     "claude_server_error")]
    [InlineData("network", "claude_network_error")]
    [InlineData("429",     "claude_rate_limited")]
    public async Task A_first_background_run_that_fails_transiently_stays_Processing_for_its_retry(string kind, string expectedCode)
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.Claude.AnalyzeThrows = Failure(kind);

        var outcome = await h.Build().RunAsync(Run(doc), CancellationToken.None);

        Assert.Equal(AnalysisRunStatus.AutoRetryPending, outcome.Status);
        Assert.Equal(expectedCode, outcome.FailureCode);
        Assert.Equal(kind == "429" ? 45 : null, outcome.RetryAfterSeconds);

        // Not terminal: the document is left exactly where the retry needs it.
        Assert.Equal("Processing", h.Store.StatusOf(doc.DocumentId));
        Assert.DoesNotContain(h.Store.Calls, c => c.Status == "Failed");

        // Recorded, never charged.
        var recorded = Assert.Single(h.Documents.ExtractionFailures);
        Assert.Equal(expectedCode, recorded.Error);
        Assert.False(recorded.CountsAsAttempt);

        // The failed call keeps its ApiCallLogs row, tied to the document.
        var row = Assert.Single(h.ApiCalls.Rows);
        Assert.False(row.IsSuccess);
        Assert.Equal(doc.DocumentId, row.DocumentId);

        // Nobody is told yet, and there is no Failed audit row: the retry's outcome is what counts.
        Assert.Empty(h.Notifier.Failed);
        Assert.DoesNotContain(h.Audit.Entries, e => e.Action == AnalysisRunFailureCodes.FailedAuditAction);
        Assert.Empty(h.Analyses.Created);
    }

    [Theory]
    [InlineData("500")]
    [InlineData("network")]
    [InlineData("429")]
    public async Task The_retry_itself_takes_todays_failure_path(string kind)
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.Claude.AnalyzeThrows = Failure(kind);

        var outcome = await h.Build().RunAsync(Run(doc) with { IsAutoRetry = true }, CancellationToken.None);

        Assert.Equal(AnalysisRunStatus.ExtractionFailed, outcome.Status);
        Assert.Equal("Failed", h.Store.StatusOf(doc.DocumentId));
        Assert.Single(h.Notifier.Failed);
        Assert.Contains(h.Audit.Entries, e => e.Action == AnalysisRunFailureCodes.FailedAuditAction);
        Assert.False(Assert.Single(h.Documents.ExtractionFailures).CountsAsAttempt);
    }

    [Theory]
    [InlineData("500")]
    [InlineData("network")]
    [InlineData("429")]
    public async Task A_synchronous_run_is_unchanged(string kind)
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.Claude.AnalyzeThrows = Failure(kind);

        var outcome = await h.Build().RunAsync(Run(doc, isBackground: false), CancellationToken.None);

        Assert.Equal(AnalysisRunStatus.ExtractionFailed, outcome.Status);
        Assert.Equal("Failed", h.Store.StatusOf(doc.DocumentId));
        Assert.Single(h.Notifier.Failed);
    }

    [Theory]
    [InlineData("timeout", "claude_timeout")]
    [InlineData("401",     "claude_auth_error")]
    [InlineData("400",     "claude_api_error")]
    [InlineData("refusal", "claude_refusal")]
    public async Task Any_other_background_failure_is_unchanged(string kind, string expectedCode)
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.Claude.AnalyzeThrows = Failure(kind);

        var outcome = await h.Build().RunAsync(Run(doc), CancellationToken.None);

        Assert.Equal(AnalysisRunStatus.ExtractionFailed, outcome.Status);
        Assert.Equal(expectedCode, outcome.FailureCode);
        Assert.Equal("Failed", h.Store.StatusOf(doc.DocumentId));
        Assert.Single(h.Notifier.Failed);
    }
}
