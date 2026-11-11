// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// Sends one legajo document to the model for classification. Deliberately NOT part of
/// <c>IClaudeService</c>: it goes through the legajo pipeline's own minimal Messages client
/// (<see cref="LegajoMessagesClient"/>: forced tool use, no thinking) and leaves the balance extraction
/// path and its prompt fingerprints untouched.
/// </summary>
public interface ILegajoClassificationService
{
    /// <summary>
    /// Classifies <paramref name="pdfBytes"/>. Never throws for an API or parsing failure — the result
    /// carries a code; only cancellation propagates. A call that reached the API writes its ApiCallLogs
    /// row (Operation LegajoClassification) before this returns.
    /// </summary>
    /// <param name="context">Tenant and document, for the ApiCallLogs row only.</param>
    /// <param name="pdfBytes">The stored PDF.</param>
    /// <param name="quality">The page-quality report; decides the read mode.</param>
    /// <param name="isStillAllowedAsync">
    /// Evaluated immediately before EVERY request (including retries). Returning false aborts with
    /// <see cref="LegajoClassificationErrors.ConsentRevoked"/> and no request is sent. The signature
    /// has no parameter for the person's name or CUIT on purpose: the request is built from the
    /// document alone.
    /// </param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<LegajoClassificationResult> ClassifyAsync(
        LegajoCallContext context,
        byte[] pdfBytes,
        PageTextQualityReport quality,
        Func<CancellationToken, Task<bool>> isStillAllowedAsync,
        CancellationToken ct);
}
