// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// Sends one legajo document to the model for EXTRACTION with its type's versioned schema (M10 Phase
/// 2b). Deliberately NOT part of <c>IClaudeService</c>: balance extraction and its prompt fingerprints
/// stay untouched. One call is one pass; the processor decides whether a second one runs.
/// </summary>
public interface ILegajoExtractionService
{
    /// <summary>The model id this instance sends.</summary>
    string Model { get; }

    /// <summary>
    /// Runs one pass. Never throws for an API, rendering or parsing failure — the result carries a
    /// code; only cancellation propagates. A call that reached the API writes its ApiCallLogs row
    /// (Operation LegajoExtraction) before this returns.
    /// </summary>
    /// <param name="context">Tenant and document, for the ApiCallLogs row only.</param>
    /// <param name="schema">The document type's schema (<see cref="LegajoExtractionPrompts.For"/>).</param>
    /// <param name="pdfBytes">The stored PDF.</param>
    /// <param name="quality">The page-quality report (page count, per-page verdicts).</param>
    /// <param name="readMode">Document or Images (<see cref="LegajoExtractionService.ChooseReadMode"/> for the first pass).</param>
    /// <param name="isStillAllowedAsync">Evaluated immediately before every request; false aborts with ConsentRevoked.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<LegajoExtractionPassResult> ExtractAsync(
        LegajoCallContext context,
        LegajoExtractionSchema schema,
        byte[] pdfBytes,
        PageTextQualityReport quality,
        string readMode,
        Func<CancellationToken, Task<bool>> isStillAllowedAsync,
        CancellationToken ct);
}
