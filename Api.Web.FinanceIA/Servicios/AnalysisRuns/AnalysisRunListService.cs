// Project : Api.Web.FinanceIA
// Created : 2026-11-03

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>
/// Default <see cref="IAnalysisRunListService"/>. Scoped.
///
/// <para>
/// WHY A FAILURE IS READ FROM TWO PLACES. A failed run leaves its reason either on the document
/// (<c>LastExtractionError</c>, written when a model call was charged — <c>claude_api_error</c>,
/// <c>claude_rate_limited</c> or an exception type name) or on its <c>Analysis.Failed</c> audit row
/// (<c>stale_processing</c>, <c>consent_required</c>, …, written when nothing was charged). Neither column
/// is cleared by a later run, so each is trusted only when it was written by THIS failure: at or after
/// the document's <c>ProcessedAt</c> (the failure time, written first) less
/// <see cref="FailureCorrelationTolerance"/>. The document code wins; then the audit reason; then
/// <see cref="AnalysisRunFailureCodes.Other"/>. The one stored reason feeds both vocabularies: <c>failureCode</c>
/// (<see cref="AnalysisRunFailureCodes.FromStoredReason"/>) and <c>failureReason</c> / <c>causeGroup</c>
/// (<see cref="AnalysisRunFailureReasons"/>). The text itself stays in memory: never in a response or a log.
/// </para>
///
/// <para>
/// canRetry applies POST /api/analyses' own document rules, in its order: the caller's role, the
/// extraction budget (<see cref="ReanalysisPolicy"/> over the attempts summed per FILE, failing open
/// like the trigger does), then a persona humana entity's active consent. The tenant's monthly
/// quota is a tenant-wide state and is not folded into each row.
/// </para>
///
/// <para>
/// attemptsRemaining is a fact about the FILE, not the caller: the per-hash attempts are read once for
/// every caller (one batched query for all rows, only when the budget is on) and feed both it and canRetry.
/// An unreadable count makes attemptsRemaining null (unknown) while canRetry keeps failing open.
/// </para>
/// </summary>
public sealed class AnalysisRunListService : IAnalysisRunListService
{
    /// <summary>Most rows each of the three lists returns.</summary>
    public const int MaxItemsPerList = 50;

    /// <summary>How far back the completed list looks.</summary>
    public static readonly TimeSpan CompletedWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// Slack between a document's ProcessedAt and the reason written right after it. Both are
    /// DateTime.UtcNow on the same process, milliseconds apart; the slack covers clock rounding only.
    /// </summary>
    public static readonly TimeSpan FailureCorrelationTolerance = TimeSpan.FromSeconds(2);

    private const string FailedStatus = "Failed";

    private static readonly string[] CreatorRoles = Roles.AnalysisCreators.Split(',');

    private readonly IAnalysisRunListRepository  _repository;
    private readonly ILegajoDocumentRepository   _legajoDocumentRepository;
    private readonly AnalysisRunRegistry         _registry;
    private readonly IOptions<BillingOptions>    _billingOptions;
    private readonly ILogger<AnalysisRunListService> _logger;

    /// <summary>Initializes a new instance of <see cref="AnalysisRunListService"/>.</summary>
    public AnalysisRunListService(
        IAnalysisRunListRepository      repository,
        ILegajoDocumentRepository       legajoDocumentRepository,
        AnalysisRunRegistry             registry,
        IOptions<BillingOptions>        billingOptions,
        ILogger<AnalysisRunListService> logger)
    {
        _repository               = repository;
        _legajoDocumentRepository = legajoDocumentRepository;
        _registry                 = registry;
        _billingOptions           = billingOptions;
        _logger                   = logger;
    }

    /// <summary>True when one of <paramref name="isInRole"/>'s roles may trigger an analysis.</summary>
    public static bool CanTrigger(Func<string, bool> isInRole)
    {
        ArgumentNullException.ThrowIfNull(isInRole);
        return CreatorRoles.Any(isInRole);
    }

    /// <inheritdoc />
    public async Task<AnalysisRunsResponse> GetRunsAsync(
        string tenantId, int days, bool callerCanTrigger, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var failedSince    = nowUtc.AddDays(-days);
        var completedSince = nowUtc - CompletedWindow;

        var running = await _repository
            .GetRunningAsync(tenantId, MaxItemsPerList, ct).ConfigureAwait(false);
        var failed = await _repository
            .GetFailedAsync(tenantId, failedSince, MaxItemsPerList, ct).ConfigureAwait(false);
        var completed = await _repository
            .GetCompletedAsync(tenantId, completedSince, MaxItemsPerList, ct).ConfigureAwait(false);

        // Every failed row needs its audit row (for the requester at least), so one read covers all.
        var audits = failed.Count == 0
            ? []
            : await _repository
                .GetFailureAuditsAsync(
                    failed.Select(f => f.DocumentId).ToList(), tenantId,
                    failedSince - FailureCorrelationTolerance, ct)
                .ConfigureAwait(false);
        var auditsByDocument = audits
            .GroupBy(a => a.DocumentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.Timestamp).ToList());

        var runInfo = running
            .Select(r => (r.DocumentId, Info: _registry.GetRunInfo(r.DocumentId, tenantId)))
            .Where(x => x.Info is not null)
            .ToDictionary(x => x.DocumentId, x => x.Info!);

        // The stored reason stays in this method: only the allowlisted vocabularies leave it.
        var failures = failed.ToDictionary(
            f => f.DocumentId,
            f => ResolveStoredReason(f, auditsByDocument.GetValueOrDefault(f.DocumentId)));

        var userIds = runInfo.Values.Select(i => i.RequestedBy)
            .Concat(failures.Values.Where(x => x.RequestedBy is not null).Select(x => x.RequestedBy!.Value))
            .Distinct()
            .ToList();
        var userNames = await _repository
            .GetUserDisplayNamesAsync(userIds, tenantId, ct).ConfigureAwait(false);

        // ── Extraction budget, per FILE (as TriggerAsync reads it): ONE read for every failed row ──
        var maxAttempts    = _billingOptions.Value?.MaxExtractionAttemptsPerDocument ?? 0;
        var attemptsByHash = failed.Count > 0 && maxAttempts > 0
            ? await ReadAttemptsByHashAsync(failed, tenantId, ct).ConfigureAwait(false)
            : null;

        var retryable = callerCanTrigger && failed.Count > 0
            ? await ResolveRetryableAsync(failed, attemptsByHash, maxAttempts, tenantId, ct).ConfigureAwait(false)
            : new HashSet<Guid>();

        var response = new AnalysisRunsResponse
        {
            WindowDays  = days,
            GeneratedAt = nowUtc,
            Running = running
                // Newest first by the best time known: the claim when this instance holds the run,
                // else the upload (the repository's own order).
                .OrderByDescending(r => runInfo.GetValueOrDefault(r.DocumentId)?.StartedAtUtc ?? r.UploadedAt)
                .Select(r =>
                {
                    var info = runInfo.GetValueOrDefault(r.DocumentId);
                    return new AnalysisRunRunningItem
                    {
                        DocumentId  = r.DocumentId,
                        EntityId    = r.EntityId,
                        EntityName  = BlankToNull(r.EntityName),
                        FileName    = r.FileName,
                        StartedAt   = info?.StartedAtUtc,
                        RequestedBy = info is null ? null : userNames.GetValueOrDefault(info.RequestedBy),
                        AutoRetry   = ToRunningAutoRetry(_registry.GetAutoRetry(r.DocumentId, tenantId))
                    };
                })
                .ToList(),
            Failed = failed
                .Select(f =>
                {
                    var failure = failures[f.DocumentId];
                    var reason  = AnalysisRunFailureReasons.FromStoredReason(failure.StoredReason);
                    return new AnalysisRunFailedItem
                    {
                        DocumentId        = f.DocumentId,
                        EntityId          = f.EntityId,
                        EntityName        = BlankToNull(f.EntityName),
                        FileName          = f.FileName,
                        StartedAt         = null,
                        RequestedBy       = failure.RequestedBy is { } userId ? userNames.GetValueOrDefault(userId) : null,
                        FailedAt          = f.ProcessedAt!.Value,
                        FailureCode       = AnalysisRunFailureCodes.FromStoredReason(failure.StoredReason),
                        CanRetry          = retryable.Contains(f.DocumentId),
                        FailureReason     = reason,
                        CauseGroup        = AnalysisRunFailureReasons.CauseGroupOf(reason),
                        AttemptsRemaining = AttemptsRemainingFor(f.FileHashSha256, attemptsByHash, maxAttempts),
                        FiscalYear        = f.FiscalYear,
                        AutoRetry         = ResolveFailedAutoRetry(f, auditsByDocument.GetValueOrDefault(f.DocumentId))
                    };
                })
                .ToList(),
            Completed = completed
                .Where(c => c.ResultId is not null && c.ProcessedAt is not null)
                .Select(c => new AnalysisRunCompletedItem
                {
                    DocumentId = c.DocumentId,
                    EntityId   = c.EntityId,
                    EntityName = BlankToNull(c.EntityName),
                    FileName   = c.FileName,
                    ResultId   = c.ResultId!.Value,
                    FinishedAt = c.ProcessedAt!.Value
                })
                .ToList()
        };

        var completedWithoutResult = completed.Count - response.Completed.Count;
        if (completedWithoutResult > 0)
        {
            _logger.LogWarning(
                "{Count} completed document(s) of tenant {TenantId} have no balance analysis result and were left out of the runs list",
                completedWithoutResult, tenantId);
        }

        // Counts only: names, file names and reasons stay out of the log.
        _logger.LogInformation(
            "Listed analysis runs for tenant {TenantId}: {Running} running, {Failed} failed in {Days} day(s), {Completed} completed",
            tenantId, response.Running.Count, response.Failed.Count, days, response.Completed.Count);

        return response;
    }

    /// <summary>
    /// The failure code and requester of one failed document, from the reason written by THIS failure
    /// (see the class remarks). Pure; internal for tests.
    /// </summary>
    internal static (string Code, Guid? RequestedBy) ResolveFailure(
        AnalysisRunDocumentProjection document, IReadOnlyList<AnalysisRunFailureAuditProjection>? auditsNewestFirst)
    {
        var (storedReason, requestedBy) = ResolveStoredReason(document, auditsNewestFirst);
        return (AnalysisRunFailureCodes.FromStoredReason(storedReason), requestedBy);
    }

    /// <summary>
    /// The STORED reason (document code, exception type name or audit reason) written by THIS failure, or
    /// null when none was, and the requester (see the class remarks). Stored text: it must be mapped through
    /// an allowlist before it leaves the service. Pure; internal for tests.
    /// </summary>
    internal static (string? StoredReason, Guid? RequestedBy) ResolveStoredReason(
        AnalysisRunDocumentProjection document, IReadOnlyList<AnalysisRunFailureAuditProjection>? auditsNewestFirst)
    {
        ArgumentNullException.ThrowIfNull(document);

        var failedAt = document.ProcessedAt ?? DateTime.MinValue;
        var earliest = failedAt == DateTime.MinValue ? DateTime.MinValue : failedAt - FailureCorrelationTolerance;

        var audit = auditsNewestFirst?.FirstOrDefault(a =>
            a.Timestamp >= earliest
            && string.Equals(a.Action, AnalysisRunFailureCodes.FailedAuditAction, StringComparison.Ordinal));

        var documentCodeIsThisFailure = document.LastExtractionFailedAt is { } recordedAt && recordedAt >= earliest;

        var storedReason = documentCodeIsThisFailure
            ? document.LastExtractionError
            : audit is not null
                ? AnalysisRunFailureCodes.ReadAuditReason(audit.NewValueJson)
                : null;

        return (storedReason, audit?.UserId);
    }

    /// <summary>
    /// The <c>autoRetry</c> of a FAILED row: <c>exhausted</c> when an <c>Analysis.AutoRetryFinished</c> row
    /// with <c>outcome: "failed"</c> was written by THIS failure (at or after its ProcessedAt less
    /// <see cref="FailureCorrelationTolerance"/>, the rule <see cref="ResolveStoredReason"/> applies), else null —
    /// no retry ran, it was abandoned, or the retried failure was followed by a later run that failed on its
    /// own. Pure; internal for tests.
    /// </summary>
    internal static AnalysisRunAutoRetry? ResolveFailedAutoRetry(
        AnalysisRunDocumentProjection document, IReadOnlyList<AnalysisRunFailureAuditProjection>? auditsNewestFirst)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (auditsNewestFirst is null || document.ProcessedAt is not { } failedAt)
            return null;

        var earliest = failedAt - FailureCorrelationTolerance;
        var finished = auditsNewestFirst.FirstOrDefault(a =>
            a.Timestamp >= earliest
            && string.Equals(a.Action, AnalysisAutoRetryPolicy.FinishedAuditAction, StringComparison.Ordinal));

        return finished is not null
               && string.Equals(ReadAutoRetryOutcome(finished.NewValueJson), AnalysisAutoRetryPolicy.OutcomeFailed, StringComparison.Ordinal)
            ? new AnalysisRunAutoRetry { State = AnalysisRunAutoRetryStates.Exhausted, ScheduledAt = null }
            : null;
    }

    /// <summary>The <c>autoRetry</c> of a RUNNING row, from this instance's registry. Pure; internal for tests.</summary>
    internal static AnalysisRunAutoRetry? ToRunningAutoRetry(AnalysisAutoRetryInfo? info) => info switch
    {
        null => null,
        { State: AnalysisAutoRetryState.Scheduled } => new AnalysisRunAutoRetry
            { State = AnalysisRunAutoRetryStates.Scheduled, ScheduledAt = info.ScheduledAtUtc },
        _ => new AnalysisRunAutoRetry
            { State = AnalysisRunAutoRetryStates.Running, ScheduledAt = info.ScheduledAtUtc },
    };

    // The "outcome" string of an Analysis.AutoRetryFinished row, or null when it has none or does not parse.
    private static string? ReadAutoRetryOutcome(string? newValueJson)
    {
        if (string.IsNullOrWhiteSpace(newValueJson))
            return null;

        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(newValueJson);
            return json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                   && json.RootElement.TryGetProperty("outcome", out var outcome)
                   && outcome.ValueKind == System.Text.Json.JsonValueKind.String
                ? outcome.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts left on one row's file, from the batched per-hash sums: null when the budget is off, the
    /// sums could not be read, or the hash is blank (a blank hash is never a match — see the repository).
    /// Pure; internal for tests.
    /// </summary>
    internal static int? AttemptsRemainingFor(
        string? fileHash, IReadOnlyDictionary<string, int>? attemptsByHash, int maxAttempts)
    {
        if (attemptsByHash is null || string.IsNullOrWhiteSpace(fileHash))
            return null;

        return AnalysisRunFailureReasons.AttemptsRemaining(maxAttempts, attemptsByHash.GetValueOrDefault(fileHash));
    }

    /// <summary>
    /// The attempts spent per file hash for every failed row, in ONE query; null when the read failed. The
    /// caller only asks when the budget is on.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, int>?> ReadAttemptsByHashAsync(
        IReadOnlyList<AnalysisRunDocumentProjection> failed, string tenantId, CancellationToken ct)
    {
        try
        {
            return await _repository
                .SumExtractionAttemptsByHashesAsync(failed.Select(f => f.FileHashSha256).ToList(), tenantId, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // TriggerAsync proceeds when this count cannot be read; so does the list (canRetry). The
            // attempts left are then unknown, and reported as null.
            _logger.LogWarning(
                "Extraction attempts could not be read for the runs list of tenant {TenantId} ({ExceptionType}); " +
                "reporting the budget as available.",
                tenantId, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// The failed documents POST /api/analyses would accept again, for a caller who may trigger.
    /// <paramref name="attemptsByHash"/> is null when the budget is off or its read failed: both fail open.
    /// </summary>
    private async Task<HashSet<Guid>> ResolveRetryableAsync(
        IReadOnlyList<AnalysisRunDocumentProjection> failed,
        IReadOnlyDictionary<string, int>?            attemptsByHash,
        int                                          maxAttempts,
        string                                       tenantId,
        CancellationToken                            ct)
    {
        // ── Consent: only entities linked to a persona humana legajo need the read ───────────
        var consentBlocked = new HashSet<Guid>();
        foreach (var entityId in failed.Where(f => f.EntityPersonLegajoId is not null).Select(f => f.EntityId).Distinct())
        {
            try
            {
                var (legajoId, hasActiveConsent) = await _legajoDocumentRepository
                    .GetEntityLegajoConsentAsync(entityId, tenantId, ct)
                    .ConfigureAwait(false);

                if (legajoId is not null && !hasActiveConsent)
                    consentBlocked.Add(entityId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fails CLOSED, unlike the budget: retrying a natural person's paperwork without
                // proven consent is the one thing the trigger must never be told it may do.
                _logger.LogWarning(
                    "Consent could not be read for entity {EntityId} tenant {TenantId} ({ExceptionType}); " +
                    "its failed runs are reported as not retryable.",
                    entityId, tenantId, ex.GetType().Name);
                consentBlocked.Add(entityId);
            }
        }

        var retryable = new HashSet<Guid>();
        foreach (var document in failed)
        {
            var attempts = attemptsByHash?.GetValueOrDefault(document.FileHashSha256) ?? 0;

            var decision = ReanalysisPolicy.Evaluate(FailedStatus, null, attempts, attemptsByHash is null ? 0 : maxAttempts);
            if (decision != ReanalysisDecision.Allow)
                continue;

            if (consentBlocked.Contains(document.EntityId))
                continue;

            retryable.Add(document.DocumentId);
        }

        return retryable;
    }

    private static string? BlankToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
