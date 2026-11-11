// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using System.Collections.Concurrent;
using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>
/// Runs the analyses POST /api/analyses accepted with <c>Prefer: respond-async</c>: drains
/// <see cref="IAnalysisJobQueue"/> and runs <see cref="IAnalysisPipeline"/> for each job in its own DI
/// scope (its own DbContext), <c>Analysis:MaxConcurrentJobs</c> at a time. Same shape as
/// LegajoPipelineWorker.
///
/// <para>
/// A job carries its tenant and requester as DATA (<see cref="AnalysisRun"/>); nothing here reads an
/// HttpContext or the tenant accessor, and the request that queued the job has nothing to cancel it
/// with — the job sees only the host's stopping token, and the pipeline stops observing even that once
/// the Claude call starts.
/// </para>
///
/// <para>
/// THE QUEUE IS IN MEMORY, and nothing durable says which documents it held, so a restart loses them.
/// What it leaves behind is a document stuck in "Processing", and the SWEEP is what releases it — at
/// startup and every <c>Analysis:StaleSweepMinutes</c>:
/// <list type="bullet">
/// <item>it reads every document in Processing (all tenants) and skips any this instance holds,
/// synchronous or background, queued or running — <see cref="AnalysisRunRegistry"/>;</item>
/// <item>Documents has no "processing started at" column, so it keeps its own clock: when it FIRST SAW
/// each untracked row. A row still Processing and untracked <c>Analysis:StaleProcessingMinutes</c>
/// later is moved to Failed — conditionally, so a run that finished meanwhile wins — audited, and its
/// UPLOADER is told (ANALYSIS_FAILED): no column records who triggered the run, and every run this
/// process knows the requester of is one it holds, so is never swept. No extraction attempt is charged:
/// whether the model was ever called is unknown, and the budget fails open everywhere else.</item>
/// </list>
/// The clock restarts with the process, so recovery after a restart takes up to the threshold plus one
/// sweep. A SINGLE App Service instance is assumed, as for the legajo worker: another instance's runs
/// are invisible here, so with more than one keep StaleProcessingMinutes above the worst-case run — which
/// grows with Anthropic:ExtractionTimeoutSeconds and the max_tokens escalation (docs/guides/extraction-model.md).
/// </para>
///
/// <para>
/// ONE AUTOMATIC RETRY (<see cref="AnalysisAutoRetryPolicy"/>). A run whose extraction failed with the
/// provider's 5xx, the network or a 429 comes back <see cref="AnalysisRunStatus.AutoRetryPending"/> with its
/// document still in Processing. The job keeps its admission slot (the tenant cap counts it, the sweep skips
/// the tracked document), audits <c>Analysis.AutoRetryScheduled</c>, waits 60 s (a 429's retry-after, up to
/// 120 s, when the provider gave one) and queues the same run again with <see cref="AnalysisRun.IsAutoRetry"/>;
/// when that run ends it audits <c>Analysis.AutoRetryFinished</c>. The retry is never retried. Like the
/// queue, the wait is in memory: a restart or a shutdown during it loses the retry, and the sweep releases
/// the document after the restart as an interrupted run. A background run can therefore last two runs plus
/// up to 120 s before it ends.
/// </para>
///
/// <para>
/// A failure in one job never stops the loop. The pipeline turns every fault into a Failed document
/// and a notification before rethrowing; this class logs what reaches it by exception TYPE only.
/// </para>
/// </summary>
public sealed class AnalysisJobWorker : BackgroundService
{
    // Reason code on the Analysis.Failed audit row of a swept document.
    private const string StaleReason = AnalysisRunFailureCodes.StoredStaleProcessing;

    private readonly IAnalysisJobQueue _queue;
    private readonly AnalysisRunRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AnalysisOptions _options;
    private readonly ILogger<AnalysisJobWorker> _logger;

    // When the sweep first saw each untracked document in Processing. Touched only by the sweep loop.
    private readonly ConcurrentDictionary<Guid, DateTime> _firstObservedAt = new();

    // The delayed re-enqueue of each scheduled automatic retry, while it waits.
    private readonly ConcurrentDictionary<Guid, Task> _pendingRetries = new();

    /// <summary>Initializes a new instance of <see cref="AnalysisJobWorker"/>.</summary>
    public AnalysisJobWorker(
        IAnalysisJobQueue queue,
        AnalysisRunRegistry registry,
        IServiceScopeFactory scopeFactory,
        IOptions<AnalysisOptions> options,
        ILogger<AnalysisJobWorker> logger)
    {
        _queue        = queue;
        _registry     = registry;
        _scopeFactory = scopeFactory;
        _options      = options.Value;
        _logger       = logger;
    }

    /// <summary>How often the sweep runs: <c>Analysis:StaleSweepMinutes</c>, at least one minute.</summary>
    internal TimeSpan SweepInterval => TimeSpan.FromMinutes(Math.Max(1, _options.StaleSweepMinutes));

    /// <summary>How long an untracked document may sit in Processing: <c>Analysis:StaleProcessingMinutes</c>, at least one minute.</summary>
    internal TimeSpan StaleAfter => TimeSpan.FromMinutes(Math.Max(1, _options.StaleProcessingMinutes));

    /// <summary>The wait before an automatic retry. A seam for tests; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> otherwise.</summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;

    /// <summary>The clock the retry's due time is read from. A seam for tests.</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>The delayed re-enqueue of a scheduled automatic retry, while it waits. For tests.</summary>
    internal Task? GetPendingRetry(Guid documentId) => _pendingRetries.GetValueOrDefault(documentId);

    /// <summary>
    /// Runs one job in its own scope, then releases its admission slot — unless the run earned its one
    /// automatic retry (<see cref="AnalysisRunStatus.AutoRetryPending"/>), in which case the slot passes to
    /// the retry and is released when the retry ends. Internal so tests can drive it.
    /// </summary>
    internal async Task ProcessOneAsync(AnalysisRun run, CancellationToken ct)
    {
        var slotHandedOver = false;
        try
        {
            if (run.IsAutoRetry)
                _registry.MarkAutoRetryRunning(run.DocumentId, run.TenantId);

            await using var scope = _scopeFactory.CreateAsyncScope();
            var pipeline = scope.ServiceProvider.GetRequiredService<IAnalysisPipeline>();

            AnalysisRunOutcome outcome;
            try
            {
                outcome = await pipeline.RunAsync(run, ct).ConfigureAwait(false);
            }
            catch (Exception) when (run.IsAutoRetry)
            {
                // The pipeline drove the document to Failed before rethrowing: the retry is spent.
                LogAutoRetryFinished(scope.ServiceProvider, run, AnalysisAutoRetryPolicy.OutcomeFailed,
                    AnalysisAutoRetryPolicy.PipelineFaultReason);
                throw;
            }

            _logger.LogInformation(
                "Background analysis for document {DocumentId} tenant {TenantId} ended {Outcome}",
                run.DocumentId, run.TenantId, outcome.Status);

            if (outcome.Status == AnalysisRunStatus.AutoRetryPending)
            {
                slotHandedOver = await ScheduleAutoRetryAsync(scope.ServiceProvider, run, outcome, ct).ConfigureAwait(false);
            }
            else if (run.IsAutoRetry)
            {
                var (result, reason) = outcome.Status switch
                {
                    AnalysisRunStatus.Completed => (AnalysisAutoRetryPolicy.OutcomeCompleted, (string?)null),
                    AnalysisRunStatus.Abandoned => (AnalysisAutoRetryPolicy.OutcomeAbandoned, null),
                    _                           => (AnalysisAutoRetryPolicy.OutcomeFailed, outcome.FailureCode),
                };
                LogAutoRetryFinished(scope.ServiceProvider, run, result, reason);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "Background analysis for document {DocumentId} tenant {TenantId} threw {ExceptionType}; the document was driven to Failed by the pipeline",
                run.DocumentId, run.TenantId, ex.GetType().Name);
        }
        finally
        {
            if (!slotHandedOver)
                _registry.ReleaseAdmission(run.TenantId, run.DocumentId);
        }
    }

    /// <summary>
    /// One pass of the stale-Processing sweep at <paramref name="nowUtc"/>. Internal so tests can run it
    /// without the loop and without waiting.
    /// </summary>
    internal async Task SweepAsync(DateTime nowUtc, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var runState = scope.ServiceProvider.GetRequiredService<IAnalysisRunStateRepository>();

            var processing = await runState.GetProcessingDocumentsAsync(ct).ConfigureAwait(false);
            var stillProcessing = processing.Select(d => d.DocumentId).ToHashSet();

            // Forget rows that left Processing, so a later run of the same document starts a fresh clock.
            foreach (var documentId in _firstObservedAt.Keys)
            {
                if (!stillProcessing.Contains(documentId))
                    _firstObservedAt.TryRemove(documentId, out _);
            }

            foreach (var document in processing)
            {
                if (_registry.IsTracked(document.DocumentId))
                {
                    // A run on this instance holds it. Drop any clock so the next untracked spell,
                    // if one ever comes, is measured from scratch.
                    _firstObservedAt.TryRemove(document.DocumentId, out _);
                    continue;
                }

                var firstSeen = _firstObservedAt.GetOrAdd(document.DocumentId, nowUtc);
                if (nowUtc - firstSeen < StaleAfter)
                    continue;

                await FailStaleAsync(scope.ServiceProvider, runState, document, nowUtc).ConfigureAwait(false);
                _firstObservedAt.TryRemove(document.DocumentId, out _);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The API must keep running even if the business database is unreachable at this instant;
            // the next sweep tries again.
            _logger.LogError("Stale-Processing analysis sweep failed ({ExceptionType}); continuing",
                ex.GetType().Name);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting before touching the database.
        await Task.Yield();

        await SweepAsync(DateTime.UtcNow, stoppingToken).ConfigureAwait(false);

        var concurrency = Math.Max(1, _options.MaxConcurrentJobs);
        var loops = Enumerable.Range(0, concurrency)
            .Select(_ => ConsumeAsync(stoppingToken))
            .Append(SweepLoopAsync(stoppingToken))
            .ToArray();

        _logger.LogInformation(
            "Analysis job worker started with concurrency {Concurrency}, stale sweep every {SweepMinutes} minute(s) after {StaleMinutes} minute(s)",
            concurrency, SweepInterval.TotalMinutes, StaleAfter.TotalMinutes);

        await Task.WhenAll(loops).ConfigureAwait(false);
    }

    /// <summary>
    /// Schedules the one automatic retry of <paramref name="run"/>: registry state, the
    /// <see cref="AnalysisAutoRetryPolicy.ScheduledAuditAction"/> row, and the delayed re-enqueue, which takes
    /// over the run's admission slot (so the tenant cap still counts it and the sweep skips its document).
    /// True when the slot was handed over. A run that is ITSELF the retry is never retried again: its
    /// document is failed here instead.
    /// </summary>
    private async Task<bool> ScheduleAutoRetryAsync(
        IServiceProvider services, AnalysisRun run, AnalysisRunOutcome outcome, CancellationToken stoppingToken)
    {
        var failureCode = outcome.FailureCode ?? AnalysisRunFailureCodes.StoredClaudeApiError;

        if (run.IsAutoRetry)
        {
            // Unreachable: the pipeline never answers AutoRetryPending for a retry. Kept so a second
            // automatic retry stays impossible whatever the pipeline does.
            await FailUnretriedAsync(services, run, failureCode).ConfigureAwait(false);
            LogAutoRetryFinished(services, run, AnalysisAutoRetryPolicy.OutcomeFailed, failureCode);
            return false;
        }

        var delay = AnalysisAutoRetryPolicy.DelayFor(failureCode, outcome.RetryAfterSeconds);
        var dueAt = UtcNow() + delay;
        _registry.MarkAutoRetryScheduled(run.DocumentId, run.TenantId, dueAt);

        _logger.LogWarning(
            "Background analysis for document {DocumentId} tenant {TenantId} failed with {FailureCode}; " +
            "one automatic retry scheduled in {DelaySeconds} s",
            run.DocumentId, run.TenantId, failureCode, (int)delay.TotalSeconds);

        var auditService = services.GetRequiredService<IAuditService>();
        _ = auditService.LogAsync(
            tenantId:     run.TenantId,
            userId:       run.RequestedBy,
            action:       AnalysisAutoRetryPolicy.ScheduledAuditAction,
            severity:     AuditSeverities.Warning,
            entityType:   "Document",
            entityId:     run.DocumentId.ToString(),
            ipAddress:    run.IpAddress,
            requestId:    run.RequestId,
            newValueJson: JsonSerializer.Serialize(new { reason = failureCode, delaySeconds = (int)delay.TotalSeconds }),
            ct:           CancellationToken.None);

        var retry = run with { IsAutoRetry = true };
        var wait  = WaitThenEnqueueAsync(retry, failureCode, delay, stoppingToken);
        _pendingRetries[run.DocumentId] = wait;
        _ = wait.ContinueWith(
            t => _pendingRetries.TryRemove(new KeyValuePair<Guid, Task>(run.DocumentId, t)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        return true;
    }

    /// <summary>
    /// Waits, then queues the retry, which inherits the admission slot. On shutdown during the wait the slot
    /// is released and the document is left in Processing — exactly like a run still queued at shutdown: the
    /// queue is in memory, and the stale sweep releases it after the restart. Never throws.
    /// </summary>
    private async Task WaitThenEnqueueAsync(
        AnalysisRun retry, string failureCode, TimeSpan delay, CancellationToken stoppingToken)
    {
        var slotHandedOver = false;
        try
        {
            await DelayAsync(delay, stoppingToken).ConfigureAwait(false);

            if (_queue.TryEnqueue(retry))
            {
                slotHandedOver = true;
                return;
            }

            // Unreachable while the retry keeps its admission slot (admission and the channel share one
            // capacity). Kept so a full channel can never strand the document in Processing.
            _logger.LogWarning(
                "Automatic retry for document {DocumentId} tenant {TenantId} could not be queued; the document is failed with {FailureCode}",
                retry.DocumentId, retry.TenantId, failureCode);

            await using var scope = _scopeFactory.CreateAsyncScope();
            await FailUnretriedAsync(scope.ServiceProvider, retry, failureCode).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Shutdown before the automatic retry of document {DocumentId} tenant {TenantId}; it stays Processing for the stale sweep",
                retry.DocumentId, retry.TenantId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "Automatic retry for document {DocumentId} tenant {TenantId} could not be scheduled ({ExceptionType})",
                retry.DocumentId, retry.TenantId, ex.GetType().Name);
        }
        finally
        {
            if (!slotHandedOver)
                _registry.ReleaseAdmission(retry.TenantId, retry.DocumentId);
        }
    }

    /// <summary>
    /// Ends a run whose automatic retry will not happen: Failed (only while still Processing), an
    /// Analysis.Failed audit row carrying the original failure code, and the requester's ANALYSIS_FAILED —
    /// what the pipeline would have done at the failure. Never throws.
    /// </summary>
    private async Task FailUnretriedAsync(IServiceProvider services, AnalysisRun run, string failureCode)
    {
        try
        {
            var runState = services.GetRequiredService<IAnalysisRunStateRepository>();
            var moved = await runState
                .TryFailProcessingAsync(run.DocumentId, run.TenantId, UtcNow(), CancellationToken.None)
                .ConfigureAwait(false);

            if (!moved)
                return;

            var auditService = services.GetRequiredService<IAuditService>();
            _ = auditService.LogAsync(
                tenantId:     run.TenantId,
                userId:       run.RequestedBy,
                action:       AnalysisRunFailureCodes.FailedAuditAction,
                severity:     AuditSeverities.Warning,
                entityType:   "Document",
                entityId:     run.DocumentId.ToString(),
                ipAddress:    run.IpAddress,
                requestId:    run.RequestId,
                newValueJson: JsonSerializer.Serialize(new { reason = failureCode }),
                ct:           CancellationToken.None);

            var notifier = services.GetRequiredService<IAnalysisCompletionNotifier>();
            await notifier.NotifyFailedAsync(
                tenantId:   run.TenantId,
                userId:     run.RequestedBy,
                documentId: run.DocumentId,
                entityId:   run.EntityId,
                ct:         CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "Could not fail document {DocumentId} tenant {TenantId} after its automatic retry was lost ({ExceptionType}); the stale sweep will release it",
                run.DocumentId, run.TenantId, ex.GetType().Name);
        }
    }

    /// <summary>The <see cref="AnalysisAutoRetryPolicy.FinishedAuditAction"/> row of a retry that ran. Codes only.</summary>
    private static void LogAutoRetryFinished(IServiceProvider services, AnalysisRun run, string outcome, string? reason)
    {
        var auditService = services.GetRequiredService<IAuditService>();
        _ = auditService.LogAsync(
            tenantId:     run.TenantId,
            userId:       run.RequestedBy,
            action:       AnalysisAutoRetryPolicy.FinishedAuditAction,
            severity:     outcome == AnalysisAutoRetryPolicy.OutcomeCompleted ? AuditSeverities.Info : AuditSeverities.Warning,
            entityType:   "Document",
            entityId:     run.DocumentId.ToString(),
            ipAddress:    run.IpAddress,
            requestId:    run.RequestId,
            newValueJson: JsonSerializer.Serialize(new { outcome, reason }),
            ct:           CancellationToken.None);
    }

    private async Task FailStaleAsync(
        IServiceProvider services, IAnalysisRunStateRepository runState, ProcessingDocument document, DateTime nowUtc)
    {
        // CancellationToken.None from here: once the row is moved, the audit row and the notification
        // must follow, shutdown or not.
        var moved = await runState
            .TryFailProcessingAsync(document.DocumentId, document.TenantId, nowUtc, CancellationToken.None)
            .ConfigureAwait(false);

        if (!moved)
            return;

        _logger.LogWarning(
            "Document {DocumentId} tenant {TenantId} was in Processing with no run on this instance for at least " +
            "{StaleMinutes} minute(s); marked Failed (reason {Reason}) and its uploader notified",
            document.DocumentId, document.TenantId, StaleAfter.TotalMinutes, StaleReason);

        var auditService = services.GetRequiredService<IAuditService>();
        _ = auditService.LogAsync(
            tenantId:     document.TenantId,
            userId:       null,
            action:       AnalysisRunFailureCodes.FailedAuditAction,
            severity:     AuditSeverities.Warning,
            entityType:   "Document",
            entityId:     document.DocumentId.ToString(),
            newValueJson: JsonSerializer.Serialize(new { reason = StaleReason }),
            ct:           CancellationToken.None);

        try
        {
            var notifier = services.GetRequiredService<IAnalysisCompletionNotifier>();
            await notifier.NotifyFailedAsync(
                tenantId:   document.TenantId,
                userId:     document.UploadedBy,
                documentId: document.DocumentId,
                entityId:   document.EntityId,
                ct:         CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Failed to emit the failure notification for stale document {DocumentId} tenant {TenantId} ({ExceptionType}). The document state is unaffected.",
                document.DocumentId, document.TenantId, ex.GetType().Name);
        }
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var run in _queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
                await ProcessOneAsync(run, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown. Runs still queued stay Processing and the sweep releases them after the restart.
        }
    }

    private async Task SweepLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await SweepAsync(DateTime.UtcNow, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
    }
}
