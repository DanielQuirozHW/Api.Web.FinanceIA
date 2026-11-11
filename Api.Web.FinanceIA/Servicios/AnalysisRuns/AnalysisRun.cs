// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>
/// Everything the long part of POST /api/analyses needs, captured as DATA while the request is still
/// alive, so <see cref="IAnalysisPipeline"/> never reads an HttpContext, a claim or the tenant
/// accessor — and so the same run can be handed to <see cref="AnalysisJobWorker"/> and finish after
/// the request has gone.
/// </summary>
/// <remarks>
/// Carries the tenant ID, not the TenantContext: that record holds decrypted Dynamics and Firebase
/// secrets the pipeline never reads, and a queued job must not keep them in memory (the same choice as
/// LegajoWorkItem).
/// </remarks>
public sealed record AnalysisRun
{
    /// <summary>Tenant slug. Scopes every read and write the run makes.</summary>
    public required string TenantId { get; init; }

    /// <summary>Users.UserId of the analyst who triggered the run — the addressee of its notifications.</summary>
    public required Guid RequestedBy { get; init; }

    /// <summary>TriggerAnalysisRequest.DocumentId.</summary>
    public required Guid DocumentId { get; init; }

    /// <summary>TriggerAnalysisRequest.EntityId.</summary>
    public required Guid EntityId { get; init; }

    /// <summary>TriggerAnalysisRequest.FiscalYear.</summary>
    public required int FiscalYear { get; init; }

    /// <summary>TriggerAnalysisRequest.RequestedAmount.</summary>
    public decimal? RequestedAmount { get; init; }

    /// <summary>Documents.StoragePath as validated by the request.</summary>
    public required string StoragePath { get; init; }

    /// <summary>Documents.DocumentType as validated by the request.</summary>
    public required string DocumentType { get; init; }

    /// <summary>Documents.FiscalYear as validated by the request.</summary>
    public int? DocumentFiscalYear { get; init; }

    /// <summary>Caller IP, for the audit rows. Null when unknown.</summary>
    public string? IpAddress { get; init; }

    /// <summary>The request's TraceIdentifier, for the audit rows.</summary>
    public string? RequestId { get; init; }

    /// <summary>When the request passed the quota gate (UTC).</summary>
    public required DateTime AcceptedAtUtc { get; init; }

    /// <summary>
    /// True when the run executes on <see cref="AnalysisJobWorker"/> after a 202, false when it runs
    /// inside the HTTP request. Every behavioural difference between the two lives behind this flag.
    /// </summary>
    public bool IsBackground { get; init; }

    /// <summary>
    /// True on the one automatic retry <see cref="AnalysisJobWorker"/> queues after a transient failure of
    /// ours (<see cref="AnalysisAutoRetryPolicy"/>): the same run, every other value unchanged. Such a run is
    /// never retried again.
    /// </summary>
    public bool IsAutoRetry { get; init; }
}
