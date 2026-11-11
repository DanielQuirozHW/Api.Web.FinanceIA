// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-19

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static Api.Web.FinanceIA.Tests.AnalysisRuns.AnalysisRunTestData;

namespace Api.Web.FinanceIA.Tests.AnalysisRuns;

/// <summary>
/// Covers <see cref="AnalysisJobWorker"/>: the concurrency limit, per-job scopes and slot release,
/// tenant isolation of a job, and the stale-Processing sweep. Built on a real
/// <see cref="ServiceCollection"/> like the legajo worker's tests.
/// </summary>
public sealed class AnalysisJobWorkerTests
{
    private static readonly DateTime T0 = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Rig
    {
        public required AnalysisJobWorker Worker { get; init; }
        public required AnalysisJobQueue Queue { get; init; }
        public required AnalysisRunRegistry Registry { get; init; }
        public required PipelineHarness Harness { get; init; }

        public void Enqueue(AnalysisRun run)
        {
            Assert.Equal(AnalysisAdmission.Admitted, Registry.TryAdmit(run.TenantId, run.DocumentId));
            Assert.True(Queue.TryEnqueue(run));
        }
    }

    private static Rig BuildRig(Func<PipelineHarness, IAnalysisPipeline>? pipeline = null, int concurrency = 2)
    {
        var harness = new PipelineHarness();
        var options = Options.Create(new AnalysisOptions
        {
            MaxConcurrentJobs      = concurrency,
            StaleProcessingMinutes = 20,
            StaleSweepMinutes      = 5
        });

        // Deliberately NO ITenantContextAccessor and no HttpContext: a job has only its own data.
        var services = new ServiceCollection();
        services.AddScoped(_ => pipeline is null ? harness.Build() : pipeline(harness));
        services.AddScoped<IAnalysisRunStateRepository>(_ => harness.RunState);
        services.AddScoped<IAuditService>(_ => harness.Audit);
        services.AddScoped<IAnalysisCompletionNotifier>(_ => harness.Notifier);
        var provider = services.BuildServiceProvider();

        var queue    = new AnalysisJobQueue(options);
        var registry = new AnalysisRunRegistry(options);
        var worker   = new AnalysisJobWorker(
            queue, registry, provider.GetRequiredService<IServiceScopeFactory>(), options,
            NullLogger<AnalysisJobWorker>.Instance);

        return new Rig { Worker = worker, Queue = queue, Registry = registry, Harness = harness };
    }

    /// <summary>Holds every run until released; records how many ran at once.</summary>
    private sealed class BlockingPipeline : IAnalysisPipeline
    {
        private int _running;
        private int _peak;
        private int _finished;

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Running => Volatile.Read(ref _running);
        public int Peak => Volatile.Read(ref _peak);
        public int Finished => Volatile.Read(ref _finished);

        public async Task<AnalysisRunOutcome> RunAsync(AnalysisRun run, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _running);
            int seen;
            while (now > (seen = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, seen) != seen) { }

            await Release.Task.ConfigureAwait(false);
            Interlocked.Decrement(ref _running);
            Interlocked.Increment(ref _finished);
            return AnalysisRunOutcome.Abandoned();
        }
    }

    private sealed class ThrowingPipeline : IAnalysisPipeline
    {
        public Task<AnalysisRunOutcome> RunAsync(AnalysisRun run, CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }

    // ── Jobs ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Worker_HonoursTheConcurrencyLimit()
    {
        var blocking = new BlockingPipeline();
        var rig = BuildRig(_ => blocking, concurrency: 2);
        for (var i = 0; i < 3; i++)
            rig.Enqueue(Run(Document(TenantA)));

        await rig.Worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => blocking.Running == 2);
            await Task.Delay(200);   // give a third consumer every chance to start, if one existed
            Assert.Equal(2, blocking.Running);

            blocking.Release.SetResult();
            await WaitUntilAsync(() => blocking.Finished == 3);
            Assert.Equal(2, blocking.Peak);
            await WaitUntilAsync(() => rig.Registry.AdmittedCount == 0);
        }
        finally
        {
            await rig.Worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_RunsQueuedJobsToCompletion_ThroughTheRealPipeline()
    {
        var rig = BuildRig();
        var doc = Document(TenantA);
        rig.Harness.Store.Add(doc);
        rig.Enqueue(Run(doc));

        await rig.Worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => rig.Harness.Store.StatusOf(doc.DocumentId) == "Completed");
            await WaitUntilAsync(() => rig.Registry.AdmittedCount == 0);
        }
        finally
        {
            await rig.Worker.StopAsync(CancellationToken.None);
        }

        Assert.Single(rig.Harness.Notifier.Completed);
        Assert.False(rig.Registry.IsTracked(doc.DocumentId));
    }

    [Fact]
    public async Task AThrowingJob_IsLogged_AndItsSlotIsReleased()
    {
        var rig = BuildRig(_ => new ThrowingPipeline());
        var run = Run(Document(TenantA));
        rig.Enqueue(run);

        await rig.Worker.ProcessOneAsync(run, CancellationToken.None);

        Assert.Equal(0, rig.Registry.AdmittedCount);
        Assert.False(rig.Registry.IsTracked(run.DocumentId));
    }

    [Fact]
    public async Task AJobForTenantA_NeverReadsOrWritesTenantB()
    {
        var rig  = BuildRig();
        var docA = Document(TenantA);
        var docB = Document(TenantB);
        rig.Harness.Store.Add(docA);
        rig.Harness.Store.Add(docB);

        await rig.Worker.ProcessOneAsync(Run(docA), CancellationToken.None);
        await rig.Worker.ProcessOneAsync(Run(docB), CancellationToken.None);

        // A forged job: tenant A's slug with tenant B's document. The tenant-scoped re-read finds
        // nothing, so B's document is never read from storage, never sent to the model, never moved.
        var forged = Run(docB) with { TenantId = TenantA, RequestedBy = Guid.NewGuid() };
        var claudeCallsBefore = rig.Harness.Claude.AnalyzeCalls;
        await rig.Worker.ProcessOneAsync(forged, CancellationToken.None);

        Assert.Equal(claudeCallsBefore, rig.Harness.Claude.AnalyzeCalls);
        Assert.Equal("Completed", rig.Harness.Store.StatusOf(docB.DocumentId));
        Assert.Equal(2, rig.Harness.Storage.Reads.Count);

        // Every call that touched a document did so under that document's own tenant, except the
        // forged one, whose only call was the read that came back empty.
        var calls = rig.Harness.Store.Calls;
        Assert.All(calls.Where(c => c.DocumentId == docA.DocumentId), c => Assert.Equal(TenantA, c.TenantId));
        var crossTenant = calls.Where(c => c.DocumentId == docB.DocumentId && c.TenantId != TenantB).ToList();
        Assert.Equal(("GetById", TenantA), (Assert.Single(crossTenant).Op, crossTenant[0].TenantId));

        Assert.Equal(new[] { TenantA, TenantB }, rig.Harness.Notifier.Completed.Select(n => n.TenantId).Order());
        Assert.Equal(TenantA, rig.Harness.Analyses.Created.Single(a => a.DocumentId == docA.DocumentId).TenantId);
        Assert.Equal(TenantB, rig.Harness.Analyses.Created.Single(a => a.DocumentId == docB.DocumentId).TenantId);
    }

    // ── Stale-Processing sweep ───────────────────────────────────────────────

    [Fact]
    public async Task Sweep_LeavesADocumentAlone_UntilItHasBeenSeenForTheThreshold()
    {
        var rig = BuildRig();
        var doc = Document(TenantA);
        rig.Harness.Store.Add(doc);

        await rig.Worker.SweepAsync(T0, CancellationToken.None);
        await rig.Worker.SweepAsync(T0.AddMinutes(19), CancellationToken.None);

        Assert.Equal("Processing", rig.Harness.Store.StatusOf(doc.DocumentId));
        Assert.Empty(rig.Harness.Notifier.Failed);

        await rig.Worker.SweepAsync(T0.AddMinutes(20), CancellationToken.None);

        Assert.Equal("Failed", rig.Harness.Store.StatusOf(doc.DocumentId));
        var notified = Assert.Single(rig.Harness.Notifier.Failed);
        Assert.Equal((TenantA, doc.UploadedBy, doc.DocumentId), notified);
        var audit = Assert.Single(rig.Harness.Audit.Entries);
        Assert.Equal("Analysis.Failed", audit.Action);
        Assert.Contains("stale_processing", audit.NewValueJson);
        // CK_AuditLogs_Severity allows Info/Warning/Critical only: an "Error" row was never stored.
        Assert.Equal(Api.Web.FinanceIA.Clases.AuditSeverities.Warning, audit.Severity);
        Assert.Empty(rig.Harness.Documents.ExtractionFailures);
    }

    [Fact]
    public async Task Sweep_NeverFailsADocumentThisInstanceHolds_AndRestartsItsClockAfterwards()
    {
        var rig = BuildRig();
        var doc = Document(TenantA);
        rig.Harness.Store.Add(doc);
        rig.Registry.Track(doc.DocumentId);        // queued or running here — sync or background

        await rig.Worker.SweepAsync(T0, CancellationToken.None);
        await rig.Worker.SweepAsync(T0.AddMinutes(90), CancellationToken.None);
        Assert.Equal("Processing", rig.Harness.Store.StatusOf(doc.DocumentId));

        rig.Registry.Release(doc.DocumentId);

        await rig.Worker.SweepAsync(T0.AddMinutes(91), CancellationToken.None);   // first untracked sighting
        await rig.Worker.SweepAsync(T0.AddMinutes(110), CancellationToken.None);
        Assert.Equal("Processing", rig.Harness.Store.StatusOf(doc.DocumentId));

        await rig.Worker.SweepAsync(T0.AddMinutes(111), CancellationToken.None);
        Assert.Equal("Failed", rig.Harness.Store.StatusOf(doc.DocumentId));
    }

    [Fact]
    public async Task Sweep_LosingTheConditionalWrite_SendsNothing()
    {
        var rig = BuildRig();
        var doc = Document(TenantA);
        rig.Harness.Store.Add(doc);
        // The run finishes on another instance in the instant between the read and the write.
        rig.Harness.RunState.BeforeConditionalWrite = d => d.ProcessingStatus = "Completed";

        await rig.Worker.SweepAsync(T0, CancellationToken.None);
        await rig.Worker.SweepAsync(T0.AddMinutes(25), CancellationToken.None);

        Assert.Equal("Completed", rig.Harness.Store.StatusOf(doc.DocumentId));
        Assert.Empty(rig.Harness.Notifier.Failed);
        Assert.Empty(rig.Harness.Audit.Entries);
    }

    [Fact]
    public async Task Sweep_ForgetsADocumentThatLeftProcessing_SoItsNextRunStartsAFreshClock()
    {
        var rig = BuildRig();
        var doc = Document(TenantA);
        rig.Harness.Store.Add(doc);

        await rig.Worker.SweepAsync(T0, CancellationToken.None);
        doc.ProcessingStatus = "Completed";
        await rig.Worker.SweepAsync(T0.AddMinutes(5), CancellationToken.None);   // pruned here
        doc.ProcessingStatus = "Processing";                                        // re-analysed elsewhere

        await rig.Worker.SweepAsync(T0.AddMinutes(21), CancellationToken.None);
        Assert.Equal("Processing", rig.Harness.Store.StatusOf(doc.DocumentId));

        await rig.Worker.SweepAsync(T0.AddMinutes(41), CancellationToken.None);
        Assert.Equal("Failed", rig.Harness.Store.StatusOf(doc.DocumentId));
    }

    [Fact]
    public async Task Sweep_ReleasesStrandedDocumentsOfEveryTenant_EachUnderItsOwnTenant()
    {
        var rig  = BuildRig();
        var docA = Document(TenantA);
        var docB = Document(TenantB);
        rig.Harness.Store.Add(docA);
        rig.Harness.Store.Add(docB);

        await rig.Worker.SweepAsync(T0, CancellationToken.None);
        await rig.Worker.SweepAsync(T0.AddMinutes(20), CancellationToken.None);

        Assert.Equal(2, rig.Harness.Notifier.Failed.Count);
        Assert.Contains((TenantA, docA.UploadedBy, docA.DocumentId), rig.Harness.Notifier.Failed);
        Assert.Contains((TenantB, docB.UploadedBy, docB.DocumentId), rig.Harness.Notifier.Failed);
        Assert.All(rig.Harness.Store.Calls.Where(c => c.Op == "TryFail"),
            c => Assert.Equal(c.DocumentId == docA.DocumentId ? TenantA : TenantB, c.TenantId));
    }

    // ── Automatic retry ──────────────────────────────────────────────────────

    /// <summary>A delay the test releases by hand; records what it was asked to wait.</summary>
    private sealed class ManualDelay
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<TimeSpan> Requested { get; } = [];

        public Task WaitAsync(TimeSpan delay, CancellationToken ct)
        {
            lock (Requested) Requested.Add(delay);
            return Gate.Task.WaitAsync(ct);
        }
    }

    private static async Task<AnalysisRun> ReadQueuedAsync(AnalysisJobQueue queue)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var queued in queue.ReadAllAsync(cts.Token))
            return queued;
        throw new TimeoutException("nothing was queued");
    }

    private static (Rig Rig, Api.Web.FinanceIA.Models.Financia.Document Doc, AnalysisRun Run, ManualDelay Delay) RetryRig()
    {
        var rig   = BuildRig();
        var delay = new ManualDelay();
        rig.Worker.DelayAsync = delay.WaitAsync;
        rig.Worker.UtcNow     = () => T0;

        var doc = Document(TenantA);
        rig.Harness.Store.Add(doc);
        var run = Run(doc) with { RequestedAmount = 1_500_000m, FiscalYear = 2023 };
        // Admitted, as the trigger does; not queued — the tests drive ProcessOneAsync themselves, so the
        // queue holds only what the worker re-enqueues.
        Assert.Equal(AnalysisAdmission.Admitted, rig.Registry.TryAdmit(run.TenantId, run.DocumentId));
        return (rig, doc, run, delay);
    }

    [Fact]
    public async Task ATransientFailure_SchedulesOneRetry_KeepsTheSlot_AndTellsNobodyYet()
    {
        var (rig, doc, run, delay) = RetryRig();
        rig.Harness.Claude.AnalyzeThrows = new ClaudeApiException("Claude API error: HTTP 503.", 503);

        await rig.Worker.ProcessOneAsync(run, CancellationToken.None);

        Assert.Equal("Processing", rig.Harness.Store.StatusOf(doc.DocumentId));
        Assert.Equal(1, rig.Registry.AdmittedCount);
        Assert.True(rig.Registry.IsTracked(doc.DocumentId));
        Assert.Equal(new[] { TimeSpan.FromSeconds(60) }, delay.Requested);

        var state = rig.Registry.GetAutoRetry(doc.DocumentId, TenantA);
        Assert.NotNull(state);
        Assert.Equal(AnalysisAutoRetryState.Scheduled, state!.State);
        Assert.Equal(T0.AddSeconds(60), state.ScheduledAtUtc);
        Assert.Null(rig.Registry.GetAutoRetry(doc.DocumentId, TenantB));

        var scheduled = Assert.Single(rig.Harness.Audit.Entries, e => e.Action == AnalysisAutoRetryPolicy.ScheduledAuditAction);
        Assert.Equal("""{"reason":"claude_server_error","delaySeconds":60}""", scheduled.NewValueJson);
        Assert.Equal(doc.DocumentId.ToString(), scheduled.EntityId);
        Assert.DoesNotContain(rig.Harness.Audit.Entries, e => e.Action == AnalysisRunFailureCodes.FailedAuditAction);
        Assert.Empty(rig.Harness.Notifier.Failed);
        Assert.NotNull(rig.Worker.GetPendingRetry(doc.DocumentId));
    }

    [Fact]
    public async Task TheRetry_IsTheSameRun_RunsOnce_AndAuditsItsCompletion()
    {
        var (rig, doc, run, delay) = RetryRig();
        rig.Harness.Claude.AnalyzeThrows = new ClaudeApiException("Claude API network error.", null, new HttpRequestException());
        await rig.Worker.ProcessOneAsync(run, CancellationToken.None);

        var pending = rig.Worker.GetPendingRetry(doc.DocumentId) ?? throw new InvalidOperationException("no retry pending");

        rig.Harness.Claude.AnalyzeThrows = null;
        delay.Gate.SetResult();

        await pending;

        var retry = await ReadQueuedAsync(rig.Queue);
        Assert.True(retry.IsAutoRetry);
        Assert.Equal(run with { IsAutoRetry = true }, retry);   // same document, amount, year, requester, acceptance time

        // While it runs, the registry says so.
        rig.Harness.Claude.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processing = rig.Worker.ProcessOneAsync(retry, CancellationToken.None);
        await WaitUntilAsync(() => rig.Harness.Claude.AnalyzeCalls == 2);
        var running = rig.Registry.GetAutoRetry(doc.DocumentId, TenantA);
        Assert.Equal(AnalysisAutoRetryState.Running, running!.State);
        Assert.Equal(T0.AddSeconds(60), running.ScheduledAtUtc);

        rig.Harness.Claude.Gate.SetResult();
        await processing;

        Assert.Equal("Completed", rig.Harness.Store.StatusOf(doc.DocumentId));
        Assert.Single(rig.Harness.Notifier.Completed);
        Assert.Empty(rig.Harness.Notifier.Failed);
        var finished = Assert.Single(rig.Harness.Audit.Entries, e => e.Action == AnalysisAutoRetryPolicy.FinishedAuditAction);
        Assert.Equal("""{"outcome":"completed","reason":null}""", finished.NewValueJson);
        Assert.Equal(0, rig.Registry.AdmittedCount);
        Assert.False(rig.Registry.IsTracked(doc.DocumentId));
        Assert.Null(rig.Registry.GetAutoRetry(doc.DocumentId, TenantA));
    }

    [Fact]
    public async Task ARetryThatFailsAgain_EndsFailed_IsAuditedAsFailed_AndIsNeverRetriedAgain()
    {
        var (rig, doc, run, delay) = RetryRig();
        rig.Harness.Claude.AnalyzeThrows = new ClaudeApiException("Claude API rate limit exceeded.", 429);
        await rig.Worker.ProcessOneAsync(run, CancellationToken.None);

        var pending = rig.Worker.GetPendingRetry(doc.DocumentId) ?? throw new InvalidOperationException("no retry pending");

        delay.Gate.SetResult();

        await pending;
        var retry = await ReadQueuedAsync(rig.Queue);
        await rig.Worker.ProcessOneAsync(retry, CancellationToken.None);

        Assert.Equal("Failed", rig.Harness.Store.StatusOf(doc.DocumentId));
        Assert.Single(rig.Harness.Notifier.Failed);
        Assert.Single(rig.Harness.Audit.Entries, e => e.Action == AnalysisAutoRetryPolicy.ScheduledAuditAction);
        var finished = Assert.Single(rig.Harness.Audit.Entries, e => e.Action == AnalysisAutoRetryPolicy.FinishedAuditAction);
        Assert.Equal("""{"outcome":"failed","reason":"claude_rate_limited"}""", finished.NewValueJson);
        Assert.Single(delay.Requested);
        Assert.Null(rig.Worker.GetPendingRetry(doc.DocumentId));
        Assert.Equal(0, rig.Registry.AdmittedCount);
        Assert.False(rig.Registry.IsTracked(doc.DocumentId));
        Assert.Equal(2, rig.Harness.Claude.AnalyzeCalls);
        Assert.All(rig.Harness.Documents.ExtractionFailures, f => Assert.False(f.CountsAsAttempt));
    }

    [Fact]
    public async Task ARetryThatThrows_IsAuditedAsAPipelineFault()
    {
        var (rig, doc, run, delay) = RetryRig();
        rig.Harness.Claude.AnalyzeThrows = new ClaudeApiException("Claude API error: HTTP 500.", 500);
        await rig.Worker.ProcessOneAsync(run, CancellationToken.None);

        var pending = rig.Worker.GetPendingRetry(doc.DocumentId) ?? throw new InvalidOperationException("no retry pending");

        delay.Gate.SetResult();

        await pending;
        var retry = await ReadQueuedAsync(rig.Queue);
        rig.Harness.Claude.AnalyzeThrows = null;
        rig.Harness.Analyses.CreateThrows = new InvalidOperationException("synthetic persistence fault");

        await rig.Worker.ProcessOneAsync(retry, CancellationToken.None);

        Assert.Equal("Failed", rig.Harness.Store.StatusOf(doc.DocumentId));
        var finished = Assert.Single(rig.Harness.Audit.Entries, e => e.Action == AnalysisAutoRetryPolicy.FinishedAuditAction);
        Assert.Equal("""{"outcome":"failed","reason":"pipeline_fault"}""", finished.NewValueJson);
        Assert.Equal(0, rig.Registry.AdmittedCount);
    }

    [Fact]
    public async Task ShutdownDuringTheWait_ReleasesTheSlot_AndLeavesTheDocumentToTheSweep()
    {
        var (rig, doc, run, _) = RetryRig();
        rig.Harness.Claude.AnalyzeThrows = new ClaudeApiException("Claude API error: HTTP 503.", 503);
        using var stopping = new CancellationTokenSource();

        await rig.Worker.ProcessOneAsync(run, stopping.Token);
        var pending = rig.Worker.GetPendingRetry(doc.DocumentId) ?? throw new InvalidOperationException("no retry pending");
        stopping.Cancel();
        await pending;

        Assert.Equal(0, rig.Registry.AdmittedCount);
        Assert.False(rig.Registry.IsTracked(doc.DocumentId));
        Assert.Equal("Processing", rig.Harness.Store.StatusOf(doc.DocumentId));
        Assert.DoesNotContain(rig.Harness.Audit.Entries, e => e.Action == AnalysisAutoRetryPolicy.FinishedAuditAction);
        Assert.Empty(rig.Harness.Notifier.Failed);

        // Untracked now, so the sweep releases it after the threshold, as any run lost to a restart.
        await rig.Worker.SweepAsync(T0, CancellationToken.None);
        await rig.Worker.SweepAsync(T0.AddMinutes(20), CancellationToken.None);
        Assert.Equal("Failed", rig.Harness.Store.StatusOf(doc.DocumentId));
    }

    [Fact]
    public async Task WhileTheRetryWaits_TheSweepLeavesTheDocumentAlone()
    {
        var (rig, doc, run, _) = RetryRig();
        rig.Harness.Claude.AnalyzeThrows = new ClaudeApiException("Claude API error: HTTP 503.", 503);
        await rig.Worker.ProcessOneAsync(run, CancellationToken.None);

        await rig.Worker.SweepAsync(T0, CancellationToken.None);
        await rig.Worker.SweepAsync(T0.AddHours(2), CancellationToken.None);

        Assert.Equal("Processing", rig.Harness.Store.StatusOf(doc.DocumentId));
    }

    /// <summary>A queue that refuses every write: the one way the delayed re-enqueue can fail.</summary>
    private sealed class RefusingQueue : IAnalysisJobQueue
    {
        public bool TryEnqueue(AnalysisRun run) => false;

        public async IAsyncEnumerable<AnalysisRun> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }
    }

    [Fact]
    public async Task ARetryThatCannotBeQueued_FailsTheDocumentWithItsOriginalCode()
    {
        var harness = new PipelineHarness();
        var options = Options.Create(new AnalysisOptions { MaxConcurrentJobs = 1, StaleProcessingMinutes = 20, StaleSweepMinutes = 5 });
        var services = new ServiceCollection();
        services.AddScoped(_ => harness.Build());
        services.AddScoped<IAnalysisPipeline>(sp => sp.GetRequiredService<AnalysisPipeline>());
        services.AddScoped<IAnalysisRunStateRepository>(_ => harness.RunState);
        services.AddScoped<IAuditService>(_ => harness.Audit);
        services.AddScoped<IAnalysisCompletionNotifier>(_ => harness.Notifier);
        var registry = new AnalysisRunRegistry(options);
        var worker   = new AnalysisJobWorker(
            new RefusingQueue(), registry, services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            options, NullLogger<AnalysisJobWorker>.Instance)
        {
            DelayAsync = (_, _) => Task.CompletedTask,
        };

        var doc = Document(TenantA);
        harness.Store.Add(doc);
        var run = Run(doc);
        Assert.Equal(AnalysisAdmission.Admitted, registry.TryAdmit(run.TenantId, run.DocumentId));
        harness.Claude.AnalyzeThrows = new ClaudeApiException("Claude API error: HTTP 503.", 503);

        await worker.ProcessOneAsync(run, CancellationToken.None);
        await WaitUntilAsync(() => registry.AdmittedCount == 0);

        Assert.Equal("Failed", harness.Store.StatusOf(doc.DocumentId));
        var failed = Assert.Single(harness.Audit.Entries, e => e.Action == AnalysisRunFailureCodes.FailedAuditAction);
        Assert.Equal("""{"reason":"claude_server_error"}""", failed.NewValueJson);
        Assert.Equal((TenantA, run.RequestedBy, doc.DocumentId), Assert.Single(harness.Notifier.Failed));
        Assert.False(registry.IsTracked(doc.DocumentId));
    }
}
