// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Collections.Concurrent;
using Api.Web.FinanceIA.Models;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// The first hosted background service in the API: drains <see cref="ILegajoPipelineQueue"/> and runs
/// <see cref="ILegajoDocumentProcessor"/> for each item, in its own DI scope (its own DbContext),
/// <c>Legajo:WorkerConcurrency</c> items at a time.
///
/// <para>
/// The queue lives in memory, so the DATABASE is what the worker recovers from:
/// <list type="bullet">
/// <item>at STARTUP it enqueues EVERY waiting legajo document, whatever its age (the channel did not
/// survive the restart — every publish is one): Queued rows, and ReadyForExtraction rows of a type with
/// an extractor whose legajo has consent; plus every Classifying or Extracting row past the stale
/// window;</item>
/// <item>every <c>Legajo:StaleMinutes</c> a SWEEP enqueues again waiting rows older than the window
/// (their channel item may have been lost) and hands Classifying rows past the window back to Queued,
/// Extracting ones back to ReadyForExtraction — never a document this instance is processing right
/// now.</item>
/// </list>
/// A duplicate queue item is harmless: only one job can claim a row. The in-flight set is
/// REFERENCE-COUNTED for the same reason — when a duplicate is picked up by a second consumer while the
/// first still runs, the duplicate's job ends at once (its claim loses) and must not take the running
/// document out of the set, or the next sweep would hand it back mid-call.
/// </para>
///
/// <para>
/// A SINGLE App Service instance is assumed. With more, the atomic claim still guarantees no document
/// is processed twice at once, but one instance's sweep cannot see another's in-flight set, so a job
/// running longer than the stale window could be requeued (and paid for) twice. Keep StaleMinutes above
/// the worst-case job — a classification is about 7 minutes, an extraction up to two passes of
/// Legajo:ExtractionTimeoutSeconds each (a timeout is not retried), about 20 minutes at the defaults, so
/// 25 or more with several instances; the default is 10, safe for a single instance.
/// </para>
///
/// <para>
/// A failure in one job never stops the loop. The processor turns every failure after its claim into
/// a Failed document; anything thrown before that is logged here by exception TYPE only.
/// </para>
/// </summary>
public sealed class LegajoPipelineWorker : BackgroundService
{
    private readonly ILegajoPipelineQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly LegajoOptions _options;
    private readonly ILogger<LegajoPipelineWorker> _logger;
    private readonly ConcurrentDictionary<Guid, int> _inFlight = new();

    /// <summary>Initializes a new instance of <see cref="LegajoPipelineWorker"/>.</summary>
    public LegajoPipelineWorker(
        ILegajoPipelineQueue queue,
        IServiceScopeFactory scopeFactory,
        IOptions<LegajoOptions> options,
        ILogger<LegajoPipelineWorker> logger)
    {
        _queue        = queue;
        _scopeFactory = scopeFactory;
        _options      = options.Value;
        _logger       = logger;
    }

    /// <summary>How often the sweep runs: <c>Legajo:StaleMinutes</c>, at least one minute.</summary>
    internal TimeSpan SweepInterval => TimeSpan.FromMinutes(Math.Max(1, _options.StaleMinutes));

    /// <summary>
    /// Recovers lost work into the queue: at startup (<paramref name="startup"/> true) every Queued
    /// row, on a sweep only the stale ones; Classifying rows past the window either way. Internal so
    /// tests can run it without the loop.
    /// </summary>
    internal async Task RecoverAsync(bool startup, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<ILegajoDocumentProcessor>();
            var inFlight = _inFlight.Keys.ToHashSet();

            foreach (var item in await processor.RecoverAsync(startup, inFlight, ct).ConfigureAwait(false))
                _queue.Enqueue(item);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The API must keep running even if the business database is unreachable at this instant;
            // the next sweep tries again.
            _logger.LogError("Legajo pipeline recovery ({RecoveryKind}) failed ({ExceptionType}); continuing",
                startup ? "startup" : "sweep", ex.GetType().Name);
        }
    }

    /// <summary>Processes one item in its own scope. Internal so tests can drive it directly.</summary>
    internal async Task ProcessOneAsync(LegajoWorkItem item, CancellationToken ct)
    {
        _inFlight.AddOrUpdate(item.DocumentId, 1, static (_, count) => count + 1);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<ILegajoDocumentProcessor>();
            await processor.ProcessAsync(item, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "Legajo pipeline job for document {DocumentId} tenant {TenantId} threw {ExceptionType}; the sweep recovers it",
                item.DocumentId, item.TenantId, ex.GetType().Name);
        }
        finally
        {
            Release(item.DocumentId);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting before touching the database.
        await Task.Yield();

        await RecoverAsync(startup: true, stoppingToken).ConfigureAwait(false);

        var concurrency = Math.Max(1, _options.WorkerConcurrency);
        var loops = Enumerable.Range(0, concurrency)
            .Select(_ => ConsumeAsync(stoppingToken))
            .Append(SweepLoopAsync(stoppingToken))
            .ToArray();

        _logger.LogInformation(
            "Legajo pipeline worker started with concurrency {Concurrency}, sweep every {SweepMinutes} minute(s)",
            concurrency, SweepInterval.TotalMinutes);

        await Task.WhenAll(loops).ConfigureAwait(false);
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in _queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
                await ProcessOneAsync(item, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown. A document mid-call stays Classifying and is recovered at the next startup.
        }
    }

    /// <summary>
    /// Drops one reference to <paramref name="documentId"/>, removing it at zero. Compare-and-set, so a
    /// job starting on the same document meanwhile is never lost.
    /// </summary>
    private void Release(Guid documentId)
    {
        while (_inFlight.TryGetValue(documentId, out var count))
        {
            if (count <= 1)
            {
                if (_inFlight.TryRemove(new KeyValuePair<Guid, int>(documentId, count)))
                    return;
            }
            else if (_inFlight.TryUpdate(documentId, count - 1, count))
            {
                return;
            }
        }
    }

    private async Task SweepLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await RecoverAsync(startup: false, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
    }
}
