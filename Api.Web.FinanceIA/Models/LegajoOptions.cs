// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// The legajo document pipeline's switches, bound from the "Legajo" configuration section.
/// Every value has a working default, so an environment with no section runs the documented
/// behaviour.
/// </summary>
public sealed record LegajoOptions
{
    /// <summary>
    /// How many documents the background worker processes at once. Default 1: classification is cheap
    /// but billed, and one App Service instance is assumed (the claim is atomic regardless).
    /// Values below 1 read as 1.
    /// </summary>
    public int WorkerConcurrency { get; init; } = 1;

    /// <summary>
    /// The stale window, in minutes, and the period of the worker's sweep. Default 10.
    /// <list type="bullet">
    /// <item>At startup every Queued document is enqueued whatever its age, and every Classifying one
    /// older than this goes back to Queued (the trace of a job that stopped mid-way).</item>
    /// <item>Every StaleMinutes the sweep does the same for Queued rows older than this and Classifying
    /// rows older than this, skipping the documents this instance is processing.</item>
    /// </list>
    /// Must stay above the worst-case job (a classification: three 120 s attempts plus backoff, about
    /// 7 minutes; an extraction: two passes of up to <see cref="ExtractionTimeoutSeconds"/> each, about
    /// 20 minutes — a timeout is not retried and ends the job) when more than one instance runs, so at
    /// least 25 there — a single instance never requeues its own in-flight documents.
    /// Values below 1 read as 1.
    /// </summary>
    public int StaleMinutes { get; init; } = 10;

    /// <summary>
    /// Below this model confidence a classified document goes to NeedsReview instead of
    /// ReadyForExtraction. Default 0.7.
    /// </summary>
    public decimal ClassificationConfidenceThreshold { get; init; } = 0.7m;

    /// <summary>
    /// Phase 2b: the output ceiling of one extraction call. Default 16000 — array items carry their
    /// provenance per row (not per field) precisely so a long F.762 fits under it. A schema that declares
    /// its own <c>MaxTokens</c> (Phase 3a: DDJJ IVA and IIBB, 24000) overrides this value.
    /// </summary>
    public int ExtractionMaxTokens { get; init; } = 16000;

    /// <summary>
    /// Phase 2b: the resolution pages are rendered at for the image read mode. Default 200. Each page is
    /// then downscaled to a 1568 px long edge (the most the model looks at, and within the per-image
    /// limit of a request with many images), so a higher value only sharpens the downscale.
    /// </summary>
    public int ExtractionImageDpi { get; init; } = 200;

    /// <summary>
    /// Phase 2b: below this confidence an extracted field (or row) goes to the review queue as
    /// low_confidence, and the document to NeedsReview. Default 0.7.
    /// </summary>
    public decimal FieldConfidenceThreshold { get; init; } = 0.7m;

    /// <summary>
    /// Phase 2b: the HTTP timeout of an extraction call, in seconds. Default 600 — up to 16k output
    /// tokens over a 40-page scan can outlast far more than the classification's 120 s. A timeout is NOT
    /// retried (5xx and network errors still are) and ends the job as <c>extraction_timeout</c> with no
    /// second pass. A job is at most two passes, so with more than one App Service instance keep
    /// <see cref="StaleMinutes"/> at 25 or more. (Streaming the response would be the proper fix for
    /// long calls; not done yet.)
    /// </summary>
    public int ExtractionTimeoutSeconds { get; init; } = 600;
}
