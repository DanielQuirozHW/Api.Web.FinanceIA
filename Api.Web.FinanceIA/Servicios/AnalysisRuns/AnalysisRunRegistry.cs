// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using System.Collections.Concurrent;
using Api.Web.FinanceIA.Models;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>The answer to <see cref="AnalysisRunRegistry.TryAdmit"/>.</summary>
public enum AnalysisAdmission
{
    /// <summary>Admitted: the document is tracked and counts against both limits until released.</summary>
    Admitted,

    /// <summary>Refused: <see cref="AnalysisOptions.QueueCapacity"/> background runs are already queued or running.</summary>
    QueueFull,

    /// <summary>Refused: the tenant already has <see cref="AnalysisOptions.MaxQueuedPerTenant"/> queued or running.</summary>
    TenantLimit
}

/// <summary>
/// What THIS instance is doing with analyses right now. Singleton.
/// <list type="bullet">
/// <item><b>Tracked documents</b> — every document with a run queued or executing here, synchronous or
/// background. Reference-counted with a compare-and-set release, like LegajoPipelineWorker's in-flight
/// set, so overlapping holders never drop each other. The stale sweep never touches a tracked document.</item>
/// <item><b>Admission</b> — the background runs queued or executing, in total and per tenant, checked
/// against <see cref="AnalysisOptions.QueueCapacity"/> and <see cref="AnalysisOptions.MaxQueuedPerTenant"/>
/// before the document is moved, so a refusal never needs a write to undo.</item>
/// <item><b>Run info</b> — who requested a tracked run and when its document was claimed
/// (<see cref="AnalysisRunInfo"/>), for GET /api/analyses/runs. Display only and best-effort: it lives
/// exactly as long as the tracking reference, and nothing decides anything on it.</item>
/// </list>
/// </summary>
public sealed class AnalysisRunRegistry
{
    private readonly ConcurrentDictionary<Guid, int> _tracked = new();
    private readonly ConcurrentDictionary<Guid, AnalysisRunInfo> _runInfo = new();
    private readonly ConcurrentDictionary<Guid, AnalysisAutoRetryInfo> _autoRetry = new();
    private readonly Dictionary<string, int> _admittedByTenant = new(StringComparer.Ordinal);
    private readonly object _admissionLock = new();
    private readonly int _queueCapacity;
    private readonly int _maxPerTenant;
    private int _admittedTotal;

    /// <summary>Initializes a new instance of <see cref="AnalysisRunRegistry"/>.</summary>
    public AnalysisRunRegistry(IOptions<AnalysisOptions> options)
    {
        _queueCapacity = Math.Max(1, options.Value.QueueCapacity);
        _maxPerTenant  = Math.Max(1, options.Value.MaxQueuedPerTenant);
    }

    /// <summary>Background runs currently admitted (queued or executing) on this instance.</summary>
    public int AdmittedCount
    {
        get
        {
            lock (_admissionLock)
            {
                return _admittedTotal;
            }
        }
    }

    /// <summary>Marks <paramref name="documentId"/> as having a run on this instance. Pair with <see cref="Release"/>.</summary>
    public void Track(Guid documentId)
        => _tracked.AddOrUpdate(documentId, 1, static (_, count) => count + 1);

    /// <summary>
    /// <see cref="Track(Guid)"/>, also remembering who requested the run and when it was claimed, until
    /// the last reference is released.
    /// </summary>
    public void Track(Guid documentId, AnalysisRunInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        // Reference first, info second: a concurrent release of an older holder then either finds the
        // count above one (and leaves the info alone) or removed its entry before this info existed.
        Track(documentId);
        _runInfo[documentId] = info;
    }

    /// <summary>
    /// The info of a run this instance holds on <paramref name="documentId"/> for
    /// <paramref name="tenantId"/>, or null when it holds none, recorded none, or the run is another
    /// tenant's.
    /// </summary>
    public AnalysisRunInfo? GetRunInfo(Guid documentId, string tenantId)
        => _runInfo.TryGetValue(documentId, out var info)
           && string.Equals(info.TenantId, tenantId, StringComparison.Ordinal)
            ? info
            : null;

    /// <summary>
    /// Drops one reference to <paramref name="documentId"/>, removing it at zero. Compare-and-set, so a
    /// holder starting on the same document meanwhile is never lost.
    /// </summary>
    public void Release(Guid documentId)
    {
        while (_tracked.TryGetValue(documentId, out var count))
        {
            if (count <= 1)
            {
                if (_tracked.TryRemove(new KeyValuePair<Guid, int>(documentId, count)))
                {
                    _runInfo.TryRemove(documentId, out _);
                    _autoRetry.TryRemove(documentId, out _);
                    return;
                }
            }
            else if (_tracked.TryUpdate(documentId, count - 1, count))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Records that the one automatic retry of the run on <paramref name="documentId"/> is scheduled for
    /// <paramref name="dueAtUtc"/>. Display only, for GET /api/analyses/runs; lives as long as the tracking
    /// reference the retry keeps, so a restart forgets it.
    /// </summary>
    public void MarkAutoRetryScheduled(Guid documentId, string tenantId, DateTime dueAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        _autoRetry[documentId] = new AnalysisAutoRetryInfo(tenantId, AnalysisAutoRetryState.Scheduled, dueAtUtc);
    }

    /// <summary>Records that the automatic retry on <paramref name="documentId"/> is now executing.</summary>
    public void MarkAutoRetryRunning(Guid documentId, string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        _autoRetry.AddOrUpdate(
            documentId,
            _ => new AnalysisAutoRetryInfo(tenantId, AnalysisAutoRetryState.Running, null),
            (_, existing) => existing with { State = AnalysisAutoRetryState.Running });
    }

    /// <summary>
    /// The automatic-retry state of <paramref name="documentId"/> on this instance for
    /// <paramref name="tenantId"/>, or null when none is scheduled or running here, or it is another tenant's.
    /// </summary>
    public AnalysisAutoRetryInfo? GetAutoRetry(Guid documentId, string tenantId)
        => _autoRetry.TryGetValue(documentId, out var info)
           && string.Equals(info.TenantId, tenantId, StringComparison.Ordinal)
            ? info
            : null;

    /// <summary>True while any run on this instance holds <paramref name="documentId"/>.</summary>
    public bool IsTracked(Guid documentId) => _tracked.ContainsKey(documentId);

    /// <summary>
    /// Admits one background run for <paramref name="tenantId"/>, or says which limit refuses it. On
    /// <see cref="AnalysisAdmission.Admitted"/> the document is also tracked; release both with
    /// <see cref="ReleaseAdmission"/>.
    /// </summary>
    public AnalysisAdmission TryAdmit(string tenantId, Guid documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        lock (_admissionLock)
        {
            if (_admittedTotal >= _queueCapacity)
                return AnalysisAdmission.QueueFull;

            _admittedByTenant.TryGetValue(tenantId, out var forTenant);
            if (forTenant >= _maxPerTenant)
                return AnalysisAdmission.TenantLimit;

            _admittedByTenant[tenantId] = forTenant + 1;
            _admittedTotal++;
        }

        Track(documentId);
        return AnalysisAdmission.Admitted;
    }

    /// <summary>
    /// <see cref="TryAdmit(string, Guid)"/>, also remembering <paramref name="info"/> when admitted.
    /// </summary>
    public AnalysisAdmission TryAdmit(string tenantId, Guid documentId, AnalysisRunInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var admission = TryAdmit(tenantId, documentId);
        if (admission == AnalysisAdmission.Admitted)
            _runInfo[documentId] = info;

        return admission;
    }

    /// <summary>Releases what <see cref="TryAdmit"/> took: one admission slot and one tracking reference.</summary>
    public void ReleaseAdmission(string tenantId, Guid documentId)
    {
        lock (_admissionLock)
        {
            if (_admittedByTenant.TryGetValue(tenantId, out var forTenant))
            {
                if (forTenant <= 1)
                    _admittedByTenant.Remove(tenantId);
                else
                    _admittedByTenant[tenantId] = forTenant - 1;

                if (_admittedTotal > 0)
                    _admittedTotal--;
            }
        }

        Release(documentId);
    }
}
