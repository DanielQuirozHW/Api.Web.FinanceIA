// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// What one <see cref="LegajoMessagesClient.SendAsync{TOutput}"/> call produced. <see cref="Output"/>
/// is the caller's reading of the tool input — personal data, never logged.
/// </summary>
/// <typeparam name="TOutput">The caller's typed reading of the tool input.</typeparam>
public sealed record LegajoMessagesResult<TOutput> where TOutput : class
{
    /// <summary>True when the model answered with a tool call the caller's reader accepted.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>A short code when <see cref="Succeeded"/> is false (<see cref="LegajoClassificationErrors"/> vocabulary).</summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// For an HTTP error, what the API's error envelope said (<see cref="AnthropicErrorInfo"/>: status,
    /// error type, a clamped message and the request id); null otherwise. Written after
    /// <see cref="ErrorCode"/> in the ApiCallLogs row and never logged (the message could echo the
    /// request). Never the raw body.
    /// </summary>
    public string? ErrorDetail { get; init; }

    /// <summary>True when at least one request reached the Messages API.</summary>
    public required bool RequestSent { get; init; }

    /// <summary>The reader's output. Null unless <see cref="Succeeded"/>.</summary>
    public TOutput? Output { get; init; }

    /// <summary>HTTP status of the last response, or null when none arrived.</summary>
    public int? HttpStatus { get; init; }

    /// <summary>Input tokens billed, 0 when unknown.</summary>
    public int InputTokens { get; init; }

    /// <summary>Output tokens billed, 0 when unknown.</summary>
    public int OutputTokens { get; init; }

    /// <summary>SHA-256 hex of the request body.</summary>
    public string InputHash { get; init; } = string.Empty;

    /// <summary>SHA-256 hex of the last response body, empty when none arrived.</summary>
    public string OutputHash { get; init; } = string.Empty;

    /// <summary>Wall-clock since the request's <see cref="LegajoMessagesRequest.Clock"/> started.</summary>
    public int DurationMs { get; init; }
}
