// Project : Api.Web.FinanceIA
// Created : 2026-05-08

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Writes outbound API call log entries to [dbo].[ApiCallLogs].
/// Implementations must be fire-and-forget safe: they must catch all exceptions
/// internally and never propagate them to callers. Log failure must never
/// interrupt the main request flow.
/// </summary>
public interface IApiCallLogService
{
    /// <summary>
    /// Appends one API call log entry.
    /// </summary>
    /// <param name="tenantId">Tenant that triggered the call.</param>
    /// <param name="service">External service name (e.g. "ClaudeChat").</param>
    /// <param name="endpoint">Endpoint or operation label (max 500 chars).</param>
    /// <param name="responseStatus">HTTP status code returned, or null.</param>
    /// <param name="inputTokens">Input token count (AI calls only), or null.</param>
    /// <param name="outputTokens">Output token count (AI calls only), or null.</param>
    /// <param name="estimatedCostUsd">Estimated USD cost, or null.</param>
    /// <param name="isSuccess">Whether the call completed successfully.</param>
    /// <param name="errorMessage">Error description on failure, or null.</param>
    /// <param name="durationMs">Round-trip duration in milliseconds.</param>
    /// <param name="operation">AI operation type (e.g. "Extraction", "Narrative", "Chat").</param>
    /// <param name="model">AI model identifier.</param>
    /// <param name="userId">User that triggered the call.</param>
    /// <param name="analysisId">Associated analysis.</param>
    /// <param name="chatMessageId">Associated chat message (assistant turn).</param>
    /// <param name="documentId">Associated document (legajo classification calls; DDL 070).</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task LogAsync(
        string   tenantId,
        string   service,
        string   endpoint,
        int?     responseStatus,
        int?     inputTokens,
        int?     outputTokens,
        decimal? estimatedCostUsd,
        bool     isSuccess,
        string?  errorMessage,
        int      durationMs,
        string?  operation     = null,
        string?  model         = null,
        Guid?    userId        = null,
        Guid?    analysisId    = null,
        long?    chatMessageId = null,
        Guid?    documentId    = null,
        CancellationToken ct   = default);
}
