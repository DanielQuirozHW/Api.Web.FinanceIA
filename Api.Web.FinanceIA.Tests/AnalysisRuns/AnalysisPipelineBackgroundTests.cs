// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-19

using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Xunit;
using static Api.Web.FinanceIA.Tests.AnalysisRuns.AnalysisRunTestData;

namespace Api.Web.FinanceIA.Tests.AnalysisRuns;

/// <summary>
/// Covers what <see cref="AnalysisPipeline"/> does differently when a run executes in the background
/// (<see cref="AnalysisRun.IsBackground"/>), on the real pipeline over in-memory fakes. The synchronous
/// behaviour is covered, unchanged, by the TriggerAsync suites (TriggerGuard, ExtractionBudget,
/// CatalogueAlerts), which drive the same pipeline through the controller.
/// </summary>
public sealed class AnalysisPipelineBackgroundTests
{
    [Fact]
    public async Task Background_Success_CompletesNotifiesOnce_AndBillsTheAdmissionMonth()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        var run = Run(doc);

        var outcome = await h.Build().RunAsync(run, CancellationToken.None);

        Assert.Equal(AnalysisRunStatus.Completed, outcome.Status);
        Assert.Equal("Completed", h.Store.StatusOf(doc.DocumentId));
        var analysis = Assert.Single(h.Analyses.Created);
        Assert.Single(h.Results.Created);
        var completed = Assert.Single(h.Notifier.Completed);
        Assert.Equal(run.RequestedBy, completed.UserId);
        Assert.Empty(h.Notifier.Failed);

        // Billed in the month the quota gate admitted it, not the month the queue started it.
        Assert.Equal(run.AcceptedAtUtc, analysis.CreatedAt);
        Assert.NotEqual(run.AcceptedAtUtc, analysis.ProcessingStartedAt);
    }

    [Fact]
    public async Task Synchronous_Success_KeepsCreatedAtAtTheProcessingStart()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);

        await h.Build().RunAsync(Run(doc, isBackground: false), CancellationToken.None);

        var analysis = Assert.Single(h.Analyses.Created);
        Assert.Equal(analysis.ProcessingStartedAt, analysis.CreatedAt);
    }

    [Fact]
    public async Task Background_ClaudeFailure_FailsChargesAndNotifiesExactlyOnce()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.Claude.AnalyzeThrows = new ClaudeApiException("down", 400);

        var outcome = await h.Build().RunAsync(Run(doc), CancellationToken.None);

        Assert.Equal(AnalysisRunStatus.ExtractionFailed, outcome.Status);
        Assert.Equal("Failed", h.Store.StatusOf(doc.DocumentId));
        Assert.Single(h.Notifier.Failed);
        var failure = Assert.Single(h.Documents.ExtractionFailures);
        Assert.Equal("claude_api_error", failure.Error);
        Assert.True(failure.CountsAsAttempt);
        Assert.Empty(h.Analyses.Created);
    }

    [Fact]
    public async Task ClaudeRateLimit_IsRecordedAsItsOwnCode_AndNotCharged()
    {
        // A 429 is told apart so GET /api/analyses/runs can report "quota"; the provider's limit says
        // nothing about the document, so it does not cost an attempt.
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.Claude.AnalyzeThrows = new ClaudeApiException("Claude API rate limit exceeded.", 429);

        // The automatic retry itself: a first background attempt would be kept Processing for its one
        // retry (AnalysisPipelineAutoRetryTests); the retry's failure takes the Failed path asserted here.
        var outcome = await h.Build().RunAsync(Run(doc) with { IsAutoRetry = true }, CancellationToken.None);

        Assert.Equal(AnalysisRunStatus.ExtractionFailed, outcome.Status);
        Assert.Equal("claude_rate_limited", Assert.Single(h.Documents.ExtractionFailures).Error);
        Assert.False(h.Documents.ExtractionFailures[0].CountsAsAttempt);
        Assert.Equal(AnalysisRunFailureCodes.Quota,
            AnalysisRunFailureCodes.FromStoredReason(h.Documents.ExtractionFailures[0].Error));
    }

    [Theory]
    [InlineData(429, "claude_rate_limited")]
    [InlineData(401, "claude_auth_error")]
    [InlineData(403, "claude_auth_error")]
    [InlineData(400, "claude_api_error")]
    [InlineData(404, "claude_api_error")]
    [InlineData(500, "claude_server_error")]
    [InlineData(503, "claude_server_error")]
    [InlineData(null, "claude_api_error")]
    public void ExtractionFailureCode_ByHttpStatus(int? status, string expected)
        => Assert.Equal(expected, AnalysisPipeline.ExtractionFailureCode(new ClaudeApiException("x", status)));

    [Fact]
    public async Task Background_StorageFault_FailsAndNotifies_WithoutCharging()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.Storage.ReadThrows = new FileNotFoundException("gone");

        await Assert.ThrowsAsync<FileNotFoundException>(() => h.Build().RunAsync(Run(doc), CancellationToken.None));

        Assert.Equal("Failed", h.Store.StatusOf(doc.DocumentId));
        // Synchronously this fault is NOT notified (the caller sees the 500); in the background nobody
        // is reading the response, so the bell is the only way the analyst learns of it.
        Assert.Single(h.Notifier.Failed);
        Assert.Empty(h.Documents.ExtractionFailures);
        Assert.Equal(0, h.Claude.AnalyzeCalls);
    }

    [Fact]
    public async Task Synchronous_StorageFault_StillDoesNotNotify()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.Storage.ReadThrows = new FileNotFoundException("gone");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => h.Build().RunAsync(Run(doc, isBackground: false), CancellationToken.None));

        Assert.Equal("Failed", h.Store.StatusOf(doc.DocumentId));
        Assert.Empty(h.Notifier.Failed);
    }

    [Fact]
    public async Task Background_FailedWriteItselfThrows_StillNotifiesExactlyOnce()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.Claude.AnalyzeThrows       = new ClaudeApiException("down", 503);
        h.Documents.FailedWriteThrows = new TimeoutException("db");

        // The automatic retry, so a 503 takes the Failed path (a first attempt would wait for its retry).
        await Assert.ThrowsAsync<TimeoutException>(
            () => h.Build().RunAsync(Run(doc) with { IsAutoRetry = true }, CancellationToken.None));

        Assert.Single(h.Notifier.Failed);
    }

    [Fact]
    public async Task Background_CancellationAfterOwnership_EndsFailed_NotStrandedInProcessing()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.Claude.AnalyzeThrows = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() => h.Build().RunAsync(Run(doc), CancellationToken.None));

        Assert.Equal("Failed", h.Store.StatusOf(doc.DocumentId));
        Assert.Single(h.Notifier.Failed);
    }

    [Fact]
    public async Task Synchronous_CancellationAfterOwnership_StillLeavesProcessing()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.Claude.AnalyzeThrows = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => h.Build().RunAsync(Run(doc, isBackground: false), CancellationToken.None));

        Assert.Equal("Processing", h.Store.StatusOf(doc.DocumentId));
        Assert.Empty(h.Notifier.Failed);
    }

    [Fact]
    public async Task Background_DocumentDeletedWhileQueued_StopsBeforeTheModel()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        doc.IsDeleted = true;
        h.Store.Add(doc);

        var outcome = await h.Build().RunAsync(Run(doc), CancellationToken.None);

        AssertAbandonedWithFailure(h, doc.DocumentId, outcome, "document_deleted");
    }

    [Fact]
    public async Task Background_ConsentWithdrawnWhileQueued_StopsBeforeTheModel()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.Legajos.AddLegajo(TenantA, consent: false, entityId: doc.EntityId);

        var outcome = await h.Build().RunAsync(Run(doc), CancellationToken.None);

        AssertAbandonedWithFailure(h, doc.DocumentId, outcome, "consent_required");
    }

    [Fact]
    public async Task Background_DocumentNoLongerProcessing_IsLeftAlone()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA, status: "Failed");
        h.Store.Add(doc);

        var outcome = await h.Build().RunAsync(Run(doc), CancellationToken.None);

        Assert.Equal(AnalysisRunStatus.Abandoned, outcome.Status);
        Assert.Equal("Failed", h.Store.StatusOf(doc.DocumentId));
        Assert.Equal(0, h.Claude.AnalyzeCalls);
        Assert.Empty(h.Storage.Reads);
        // Whoever moved it owns that outcome: no second notification, no audit row from this run.
        Assert.Empty(h.Notifier.Failed);
        Assert.Empty(h.Audit.Entries);
    }

    [Fact]
    public async Task Background_ReadsTheDocumentAsItIsNow_NotAsItWasQueued()
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        var run = Run(doc) with { StoragePath = "/stale/path.pdf" };

        await h.Build().RunAsync(run, CancellationToken.None);

        Assert.Equal(doc.StoragePath, Assert.Single(h.Storage.Reads));
    }

    private static void AssertAbandonedWithFailure(PipelineHarness h, Guid documentId, AnalysisRunOutcome outcome, string reason)
    {
        Assert.Equal(AnalysisRunStatus.Abandoned, outcome.Status);
        Assert.Equal("Failed", h.Store.StatusOf(documentId));
        Assert.Equal(0, h.Claude.AnalyzeCalls);
        Assert.Empty(h.Storage.Reads);
        Assert.Empty(h.Documents.ExtractionFailures);
        Assert.Single(h.Notifier.Failed);
        var audit = Assert.Single(h.Audit.Entries);
        Assert.Equal("Analysis.Failed", audit.Action);
        Assert.Contains(reason, audit.NewValueJson);
    }
}
