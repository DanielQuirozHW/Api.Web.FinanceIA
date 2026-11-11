// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// The extraction pass's own failure codes (M10 Phase 2b), and the STAGE marker it writes to
/// Documents.LastExtractionError. The transport codes (timeout, 429, refusal …) are the shared
/// <see cref="LegajoClassificationErrors"/> vocabulary.
///
/// <para>
/// Every code an extraction writes to the document carries the <see cref="StagePrefix"/>
/// (<c>extraction_rate_limited</c>, <c>extraction_too_many_pages</c> …). That prefix is what lets
/// Reprocess send a Failed document back to the stage that failed — ReadyForExtraction, never Queued —
/// so a classification an analyst confirmed is never silently re-run. Codes, never messages: the
/// column must carry no PII and no document content.
/// </para>
/// </summary>
public static class LegajoExtractionErrors
{
    /// <summary>Marks a Documents.LastExtractionError written by the extraction stage.</summary>
    public const string StagePrefix = "extraction_";

    /// <summary>The PDF has more pages than the extraction reads. No request sent.</summary>
    public const string TooManyPages = "too_many_pages";

    /// <summary>The rendered pages would exceed the API's request size. No request sent.</summary>
    public const string RequestTooLarge = "request_too_large";

    /// <summary>
    /// The call ran past <c>Legajo:ExtractionTimeoutSeconds</c>. Not retried and no second pass: the same
    /// request would most likely run as long again. Already carries the stage prefix.
    /// </summary>
    public const string Timeout = "extraction_timeout";

    /// <summary>The tool input did not match the schema (after the image pass too, when one ran).</summary>
    public const string InvalidOutput = "invalid_output";

    /// <summary>The code as the extraction stage writes it to the document.</summary>
    public static string ForDocument(string code)
        => code.StartsWith(StagePrefix, StringComparison.Ordinal) ? code : StagePrefix + code;

    /// <summary>True when a Documents.LastExtractionError was written by the extraction stage.</summary>
    public static bool IsExtractionStage(string? lastError)
        => lastError is not null && lastError.StartsWith(StagePrefix, StringComparison.Ordinal);
}
