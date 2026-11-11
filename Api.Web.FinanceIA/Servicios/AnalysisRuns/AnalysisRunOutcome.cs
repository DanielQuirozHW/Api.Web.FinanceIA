// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>How a run of <see cref="IAnalysisPipeline"/> ended without throwing.</summary>
public enum AnalysisRunStatus
{
    /// <summary>Persisted, document Completed, requester notified.</summary>
    Completed,

    /// <summary>
    /// Claude could not extract the balance: document Failed, budget charged, requester notified.
    /// The synchronous caller answers 502.
    /// </summary>
    ExtractionFailed,

    /// <summary>
    /// Background only: by the time the job ran, the document no longer qualified (deleted, consent
    /// withdrawn, or no longer this run's to process). Nothing was sent to the model or charged.
    /// </summary>
    Abandoned,

    /// <summary>
    /// Background only: the extraction failed for a transient reason of ours
    /// (<see cref="AnalysisAutoRetryPolicy.EligibleCodes"/>) and the run earns its one automatic retry. The
    /// document is LEFT in Processing, the failure is recorded without spending an attempt, the failed call
    /// has its ApiCallLogs row, and nobody has been told: <see cref="AnalysisJobWorker"/> schedules the retry,
    /// and the retry's own outcome is what reaches the requester.
    /// </summary>
    AutoRetryPending
}

/// <summary>
/// The result of <see cref="IAnalysisPipeline.RunAsync"/>. A fault the run could not absorb is not an
/// outcome: it is rethrown after the document has been driven to a terminal state, exactly as the
/// controller always did.
/// </summary>
public sealed record AnalysisRunOutcome
{
    private AnalysisRunOutcome(
        AnalysisRunStatus status, Analysis? analysis, AnalysisResult? result, string entityName,
        string? failureCode = null, int? retryAfterSeconds = null)
    {
        Status            = status;
        Analysis          = analysis;
        Result            = result;
        EntityName        = entityName;
        FailureCode       = failureCode;
        RetryAfterSeconds = retryAfterSeconds;
    }

    /// <summary>How the run ended.</summary>
    public AnalysisRunStatus Status { get; }

    /// <summary>The persisted analysis. Set only when <see cref="Status"/> is Completed.</summary>
    public Analysis? Analysis { get; }

    /// <summary>The persisted result. Set only when <see cref="Status"/> is Completed.</summary>
    public AnalysisResult? Result { get; }

    /// <summary>The entity's business name, empty when it could not be resolved.</summary>
    public string EntityName { get; }

    /// <summary>
    /// The <c>Documents.LastExtractionError</c> code of an ExtractionFailed run (an
    /// <see cref="AnalysisRunFailureCodes"/> <c>Stored*</c> value), so the synchronous caller can word its
    /// answer. Null for every other outcome. A code, never a message.
    /// </summary>
    public string? FailureCode { get; }

    /// <summary>
    /// The provider's retry-after, in seconds, of an <see cref="AnalysisRunStatus.AutoRetryPending"/> run when
    /// it gave one; null otherwise. <see cref="AnalysisAutoRetryPolicy.DelayFor"/> decides what to do with it.
    /// </summary>
    public int? RetryAfterSeconds { get; }

    /// <summary>A completed run.</summary>
    public static AnalysisRunOutcome Completed(Analysis analysis, AnalysisResult result, string entityName)
        => new(AnalysisRunStatus.Completed, analysis, result, entityName);

    /// <summary>A run whose Claude extraction failed, with the stored code of that failure when known.</summary>
    public static AnalysisRunOutcome ExtractionFailed(string? failureCode = null)
        => new(AnalysisRunStatus.ExtractionFailed, null, null, string.Empty, failureCode);

    /// <summary>A background run that found its document no longer qualified.</summary>
    public static AnalysisRunOutcome Abandoned()
        => new(AnalysisRunStatus.Abandoned, null, null, string.Empty);

    /// <summary>A background run whose transient failure earns the one automatic retry (document left in Processing).</summary>
    public static AnalysisRunOutcome AutoRetryPending(string failureCode, int? retryAfterSeconds = null)
        => new(AnalysisRunStatus.AutoRetryPending, null, null, string.Empty, failureCode, retryAfterSeconds);
}
