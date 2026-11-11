// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// The outcome of one classification attempt, successful or not. Carries everything the caller must
/// persist (run row, ApiCallLogs row) and nothing it must not log: <see cref="Output"/> holds PII and
/// is only ever written to LegajoExtractions.ResultJson.
/// </summary>
public sealed record LegajoClassificationResult
{
    /// <summary>True when the model answered with a parseable tool call.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>
    /// A short sanitized reason code when <see cref="Succeeded"/> is false (see
    /// <see cref="LegajoClassificationErrors"/>). Never an exception message, never response content.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>True when at least one request reached the Messages API (so tokens may have been billed).</summary>
    public required bool RequestSent { get; init; }

    /// <summary>The parsed reading. Null unless <see cref="Succeeded"/>.</summary>
    public LegajoClassificationOutput? Output { get; init; }

    /// <summary>Exact model id sent.</summary>
    public required string Model { get; init; }

    /// <summary>One of <c>Models.Financia.LegajoReadModes</c>.</summary>
    public required string ReadMode { get; init; }

    /// <summary>HTTP status of the last response, or null when none arrived.</summary>
    public int? HttpStatus { get; init; }

    /// <summary>Input tokens billed (from the response's usage), 0 when unknown.</summary>
    public int InputTokens { get; init; }

    /// <summary>Output tokens billed, 0 when unknown.</summary>
    public int OutputTokens { get; init; }

    /// <summary>SHA-256 hex of the request body, empty when no request was built.</summary>
    public string InputHash { get; init; } = string.Empty;

    /// <summary>SHA-256 hex of the last response body, empty when none arrived.</summary>
    public string OutputHash { get; init; } = string.Empty;

    /// <summary>Wall-clock of the whole attempt including retries.</summary>
    public int DurationMs { get; init; }
}
