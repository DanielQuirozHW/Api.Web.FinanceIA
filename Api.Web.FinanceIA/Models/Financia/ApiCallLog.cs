// Project : Api.Web.FinanceIA
// Created : 2026-05-08

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[ApiCallLogs].
/// Append-only log of outbound API calls (Claude, BCRA, Dynamics, BYMA).
/// Database First — schema changes via DDL scripts only.
/// </summary>
public sealed class ApiCallLog
{
    /// <summary>Primary key (auto-increment).</summary>
    public long ApiCallLogId { get; set; }

    /// <summary>Tenant that triggered the call.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// External service name. Allowed values: Claude | ClaudeChat | BCRA | Dynamics | BYMA.
    /// Constrained by CK_ApiCallLogs_Service.
    /// </summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>Endpoint or operation identifier (max 500 chars).</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>SHA-256 hex hash of the serialized request body. Null when not applicable.</summary>
    public string? RequestBodyHash { get; set; }

    /// <summary>HTTP status code returned by the external service. Null for non-HTTP operations.</summary>
    public int? ResponseStatus { get; set; }

    /// <summary>SHA-256 hex hash of the response body. Null when not applicable.</summary>
    public string? ResponseBodyHash { get; set; }

    /// <summary>Round-trip duration in milliseconds.</summary>
    public int? DurationMs { get; set; }

    /// <summary>Input token count (AI services only).</summary>
    public int? InputTokens { get; set; }

    /// <summary>Output token count (AI services only).</summary>
    public int? OutputTokens { get; set; }

    /// <summary>Estimated cost in USD based on token counts and published pricing.</summary>
    public decimal? EstimatedCostUsd { get; set; }

    /// <summary>True when the call completed successfully.</summary>
    public bool IsSuccess { get; set; }

    /// <summary>Error description when <see cref="IsSuccess"/> is false.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>UTC timestamp when the call was made.</summary>
    public DateTime CalledAt { get; set; }

    /// <summary>AI operation type (e.g. "Extraction", "Narrative", "Chat").</summary>
    public string? Operation { get; set; }

    /// <summary>AI model identifier (e.g. "claude-sonnet-4-6").</summary>
    public string? Model { get; set; }

    /// <summary>User that triggered the call, or null.</summary>
    public Guid? UserId { get; set; }

    /// <summary>Associated analysis, or null.</summary>
    public Guid? AnalysisId { get; set; }

    /// <summary>Associated chat message (assistant turn), or null.</summary>
    public long? ChatMessageId { get; set; }

    /// <summary>
    /// Associated document (DDL 070), or null. Set by the legajo classification calls; no FK, like
    /// <see cref="AnalysisId"/>, so a log row never blocks a document operation.
    /// </summary>
    public Guid? DocumentId { get; set; }
}
