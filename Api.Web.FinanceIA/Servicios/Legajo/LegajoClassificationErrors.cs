// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// The short codes the legajo pipeline writes to Documents.LastExtractionError and to a failed run.
/// Codes, never messages: the column must carry no PII and no document content.
/// </summary>
public static class LegajoClassificationErrors
{
    /// <summary>Anthropic:ApiKey is not configured. No request sent.</summary>
    public const string NotConfigured = "classification_not_configured";

    /// <summary>The PDF could not be rendered for the image read mode. No request sent.</summary>
    public const string PdfUnreadable = "pdf_unreadable";

    /// <summary>Consent disappeared before the call. No request sent; the document returns to Uploaded.</summary>
    public const string ConsentRevoked = "consent_revoked";

    /// <summary>Every attempt timed out.</summary>
    public const string Timeout = "claude_timeout";

    /// <summary>Every attempt failed at the network level.</summary>
    public const string NetworkError = "claude_network_error";

    /// <summary>HTTP 429. No tokens were billed, so it does NOT consume the per-file attempt budget.</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>Any other non-success HTTP status.</summary>
    public const string ApiError = "claude_api_error";

    /// <summary>The response had no tool_use block for our tool, or it did not parse.</summary>
    public const string InvalidResponse = "claude_invalid_response";

    /// <summary>stop_reason max_tokens: the tool input may be truncated.</summary>
    public const string Truncated = "claude_truncated";

    /// <summary>stop_reason refusal.</summary>
    public const string Refused = "claude_refused";

    /// <summary>The per-file attempt budget (Billing:MaxExtractionAttemptsPerDocument) is spent. No request sent.</summary>
    public const string AttemptsExhausted = "attempts_exhausted";

    /// <summary>The stored file could not be read. No request sent.</summary>
    public const string StorageUnavailable = "storage_unavailable";

    /// <summary>An unexpected exception after the claim (its type is logged, never its message).</summary>
    public const string Unexpected = "unexpected";
}
