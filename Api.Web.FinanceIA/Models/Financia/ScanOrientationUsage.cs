// Project : Api.Web.FinanceIA
// Created : 2026-11-03

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The billable side of one scan-orientation call (<c>IScanOrientationDetector</c>): which model
/// answered, its own tokens, how long it took and how it ended. Present only when a request reached the
/// API. Carried on <see cref="ClaudeAnalysisResult.OrientationDetection"/> so the pipeline can write its
/// own ApiCallLogs row, priced with <see cref="Model"/> — its tokens are NEVER added to the
/// extraction's. Counts and codes only: never a page, a figure or a response body.
/// </summary>
public sealed record ScanOrientationUsage
{
    /// <summary>Model id the request was sent to.</summary>
    public required string Model { get; init; }

    /// <summary>Input tokens the API reported (0 when it reported none).</summary>
    public int InputTokens { get; init; }

    /// <summary>Output tokens the API reported (0 when it reported none).</summary>
    public int OutputTokens { get; init; }

    /// <summary>Wall-clock time of the whole detection: thumbnails, request and parsing.</summary>
    public int DurationMs { get; init; }

    /// <summary>True when the API answered with a readable tool call.</summary>
    public bool Succeeded { get; init; }

    /// <summary>The HTTP status, or null when no response came back (timeout, network error).</summary>
    public int? HttpStatus { get; init; }

    /// <summary>A code this project writes (e.g. claude_timeout), null on success. Never the API's body.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// For an HTTP error, what the API's error envelope said (<see cref="Servicios.AnthropicErrorInfo"/>:
    /// status, error type, a clamped message and the request id); null otherwise. Written beside
    /// <see cref="ErrorCode"/> in the ApiCallLogs row. Never the raw body.
    /// </summary>
    public string? ErrorDetail { get; init; }

    /// <summary>Pages the request asked about.</summary>
    public int PagesAsked { get; init; }

    /// <summary>Pages the answer judged (a known rotation for a page that was asked about).</summary>
    public int PagesAnswered { get; init; }
}
