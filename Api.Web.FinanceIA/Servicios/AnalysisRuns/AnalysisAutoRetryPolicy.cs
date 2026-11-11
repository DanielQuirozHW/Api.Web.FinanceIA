// Project : Api.Web.FinanceIA
// Created : 2026-11-09

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>
/// The single automatic retry of a BACKGROUND analysis run that failed for a reason of OURS that is
/// expected to pass on its own: the provider answered 5xx (<c>claude_server_error</c>), the network failed
/// (<c>claude_network_error</c>) or the provider rate-limited us (<c>claude_rate_limited</c>). Pure.
///
/// <para>
/// ONE retry, never two: the retry is the same <see cref="AnalysisRun"/> with
/// <see cref="AnalysisRun.IsAutoRetry"/> set, and such a run is never eligible again. A later manual
/// "Reintentar" is a new run, and may get its own single retry. No other failure code retries: a timeout,
/// a refusal, an unreadable answer, our own output cap or our key would fail the same way again, and the
/// first three are the document's. The synchronous trigger never retries — its caller is on the screen
/// and has the Portal's "Reintentar".
/// </para>
///
/// <para>
/// None of the eligible codes spends the file's extraction budget
/// (<c>AnalysisPipeline.ExtractionFailureCountsAsAttempt</c>), so the retry costs the analyst nothing.
/// </para>
/// </summary>
public static class AnalysisAutoRetryPolicy
{
    /// <summary>The wait before the retry when nothing better is known.</summary>
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(60);

    /// <summary>The longest a provider's retry-after may hold a 429 retry back.</summary>
    public static readonly TimeSpan MaxRateLimitDelay = TimeSpan.FromSeconds(120);

    /// <summary>
    /// AuditLogs.Action written when the retry is scheduled. NewValueJson:
    /// <c>{"reason": "&lt;stored code&gt;", "delaySeconds": n}</c>.
    /// </summary>
    public const string ScheduledAuditAction = "Analysis.AutoRetryScheduled";

    /// <summary>
    /// AuditLogs.Action written when the retry has run. NewValueJson:
    /// <c>{"outcome": "completed|failed|abandoned", "reason": "&lt;stored code&gt;"|"pipeline_fault"|null}</c>.
    /// GET /api/analyses/runs reads it to report <c>autoRetry.state = "exhausted"</c>.
    /// </summary>
    public const string FinishedAuditAction = "Analysis.AutoRetryFinished";

    /// <summary>Outcome of a retry that persisted its analysis.</summary>
    public const string OutcomeCompleted = "completed";

    /// <summary>Outcome of a retry that ended Failed (the model failed again, or the pipeline faulted).</summary>
    public const string OutcomeFailed = "failed";

    /// <summary>Outcome of a retry whose document no longer qualified when it ran (deleted, consent withdrawn).</summary>
    public const string OutcomeAbandoned = "abandoned";

    /// <summary>The <c>reason</c> of a failed retry that ended in an exception rather than a stored code.</summary>
    public const string PipelineFaultReason = "pipeline_fault";

    /// <summary>The stored failure codes that earn the one automatic retry.</summary>
    public static IReadOnlySet<string> EligibleCodes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        AnalysisRunFailureCodes.StoredClaudeServerError,
        AnalysisRunFailureCodes.StoredClaudeNetworkError,
        AnalysisRunFailureCodes.StoredClaudeRateLimited,
    };

    /// <summary>
    /// True when a run that failed with <paramref name="failureCode"/> is retried once automatically: a
    /// background run, not itself the retry, failed with one of <see cref="EligibleCodes"/>.
    /// </summary>
    public static bool IsEligible(AnalysisRun run, string? failureCode)
    {
        ArgumentNullException.ThrowIfNull(run);

        return run.IsBackground
            && !run.IsAutoRetry
            && failureCode is not null
            && EligibleCodes.Contains(failureCode);
    }

    /// <summary>
    /// The wait before the retry: <see cref="DefaultDelay"/>, except a 429 for which the provider said when to
    /// come back (<paramref name="retryAfterSeconds"/> &gt; 0), which waits that long up to
    /// <see cref="MaxRateLimitDelay"/>. A retry-after on any other code is ignored.
    /// </summary>
    public static TimeSpan DelayFor(string? failureCode, int? retryAfterSeconds)
    {
        if (string.Equals(failureCode, AnalysisRunFailureCodes.StoredClaudeRateLimited, StringComparison.Ordinal)
            && retryAfterSeconds is > 0)
        {
            var requested = TimeSpan.FromSeconds(retryAfterSeconds.Value);
            return requested > MaxRateLimitDelay ? MaxRateLimitDelay : requested;
        }

        return DefaultDelay;
    }
}
