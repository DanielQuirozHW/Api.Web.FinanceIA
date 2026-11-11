// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Diagnostics;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// One call of <see cref="LegajoMessagesClient.SendAsync{TOutput}"/>: the serialized body plus what the
/// client needs to send it and to write its ApiCallLogs row. The body is built by the caller with
/// <see cref="LegajoMessagesClient.BuildRequestJson"/>, so each pass owns its own prompt and schema.
/// </summary>
public sealed record LegajoMessagesRequest
{
    /// <summary>ApiCallLogs.Operation: <see cref="LegajoMessagesClient.ClassificationOperation"/> or <see cref="LegajoMessagesClient.ExtractionOperation"/>.</summary>
    public required string Operation { get; init; }

    /// <summary>Tenant slug for the ApiCallLogs row.</summary>
    public required string TenantId { get; init; }

    /// <summary>The document read, for ApiCallLogs.DocumentId.</summary>
    public required Guid DocumentId { get; init; }

    /// <summary>Exact model id in <see cref="RequestJson"/>; used for the cost estimate.</summary>
    public required string Model { get; init; }

    /// <summary><c>Anthropic:ApiKey</c>. A secret: never logged.</summary>
    public required string ApiKey { get; init; }

    /// <summary>The named HttpClient to send on (its Timeout is the per-attempt limit).</summary>
    public required string HttpClientName { get; init; }

    /// <summary>The serialized Messages body.</summary>
    public required string RequestJson { get; init; }

    /// <summary>The one tool the request forces; the tool_use block is found by this name.</summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// Started by the caller before it built the request, so the reported duration covers the whole
    /// attempt (rendering included), as Phase 2a measured it.
    /// </summary>
    public required Stopwatch Clock { get; init; }

    /// <summary>
    /// Whether a timed-out attempt is retried like a network error or a 5xx. True for the
    /// classification (a two-minute call). False for the extraction: a request that ran for
    /// <c>Legajo:ExtractionTimeoutSeconds</c> will most likely run as long again, so three attempts would
    /// triple a worst case that is already ten minutes.
    /// </summary>
    public bool RetryOnTimeout { get; init; } = true;

    /// <summary>The error code a timeout ends the call with.</summary>
    public string TimeoutErrorCode { get; init; } = LegajoClassificationErrors.Timeout;
}
