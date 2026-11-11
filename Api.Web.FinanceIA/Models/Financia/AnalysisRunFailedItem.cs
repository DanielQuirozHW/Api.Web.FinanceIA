// Project : Api.Web.FinanceIA
// Created : 2026-11-03

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>One document whose run failed, as GET /api/analyses/runs lists it.</summary>
public sealed record AnalysisRunFailedItem
{
    public required Guid DocumentId { get; init; }

    public required Guid EntityId { get; init; }

    /// <summary>The entity's business name; null when it cannot be resolved.</summary>
    public string? EntityName { get; init; }

    /// <summary>The document's original file name.</summary>
    public required string FileName { get; init; }

    /// <summary>When the failed run started. Nothing durable records it, so today it is always null.</summary>
    public DateTime? StartedAt { get; init; }

    /// <summary>
    /// Display name of the analyst who triggered the failed run, from its Analysis.Failed audit row;
    /// null when unknown (a run the stale sweep released, a fault that wrote no audit row).
    /// </summary>
    public string? RequestedBy { get; init; }

    /// <summary>When the run failed (Documents.ProcessedAt of a Failed document).</summary>
    public required DateTime FailedAt { get; init; }

    /// <summary>
    /// One of <c>interrupted</c>, <c>consent_required</c>, <c>model_error</c>, <c>quota</c>,
    /// <c>other</c> (<c>AnalysisRunFailureCodes.All</c>). Never the stored reason text.
    /// </summary>
    public required string FailureCode { get; init; }

    /// <summary>
    /// True when POST /api/analyses would accept this document again for the caller: the caller may
    /// trigger analyses, the file has extraction attempts left, and a persona humana entity has active
    /// consent. The tenant's monthly analysis quota is NOT reflected (GET /api/billing/usage).
    /// </summary>
    public required bool CanRetry { get; init; }

    /// <summary>
    /// What happened, finer than <see cref="FailureCode"/> (which keeps folding every model failure into
    /// <c>model_error</c>). One of <c>AnalysisRunFailureReasons.All</c>:
    /// <c>no_financial_statements</c> (the file holds no readable balance),
    /// <c>unreadable_document</c> (the model declined it or read something that is not a balance),
    /// <c>file_rejected</c> (the provider rejected the request built from the file),
    /// <c>timeout</c>, <c>service_unavailable</c> (provider 5xx, network, rate limit, our key),
    /// <c>reading_failed</c> (we could not read the model's answer, or it hit our output cap),
    /// <c>interrupted</c>, <c>consent_required</c>, <c>other</c> (anything this build does not
    /// recognise). Never the stored reason text. Null only from a build that predates the field.
    /// </summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// Whose move it is: <c>document</c> for <c>no_financial_statements</c>, <c>unreadable_document</c> and
    /// <c>file_rejected</c> (upload another file); <c>ours</c> for every other reason (retry, or tell
    /// support). One of <c>AnalysisRunFailureReasons.CauseGroups</c>.
    /// </summary>
    public string? CauseGroup { get; init; }

    /// <summary>
    /// Extraction attempts this FILE has left: max(0, Billing:MaxExtractionAttemptsPerDocument − attempts
    /// spent on every non-deleted document of the tenant with the same content hash). Null when the budget is
    /// off (≤ 0), the file's hash is unknown, or the attempts could not be read. Reported to every caller,
    /// unlike <see cref="CanRetry"/>, which also folds in the caller's role and consent.
    /// </summary>
    public int? AttemptsRemaining { get; init; }

    /// <summary>
    /// The document's fiscal year (Documents.FiscalYear, as given at upload); null when the document has none.
    /// With <see cref="CanRetry"/> true and this not null, the Portal retries in one click with POST
    /// /api/analyses <c>{ documentId, entityId, fiscalYear }</c> and no <c>requestedAmount</c> (the trigger
    /// accepts a null amount). When it is null, the Portal asks the analyst for the year.
    /// </summary>
    public int? FiscalYear { get; init; }

    /// <summary>
    /// <c>{ "state": "exhausted", "scheduledAt": null }</c> when this failure is that of the run's automatic
    /// retry (its first attempt had failed for a transient reason of ours and the retry failed too). Every
    /// other field is reported exactly as without the retry. Null when no automatic retry applies.
    /// </summary>
    public AnalysisRunAutoRetry? AutoRetry { get; init; }
}
