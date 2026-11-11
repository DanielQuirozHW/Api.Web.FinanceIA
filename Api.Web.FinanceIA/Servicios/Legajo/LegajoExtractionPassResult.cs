// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// One extraction pass over a document, successful or not. <see cref="Reading"/> is personal data and
/// is only ever written to LegajoExtractions.ResultJson.
/// </summary>
public sealed record LegajoExtractionPassResult
{
    /// <summary>True when the model answered and its tool input matched the schema.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>A short code when <see cref="Succeeded"/> is false.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>True when at least one request reached the Messages API.</summary>
    public required bool RequestSent { get; init; }

    /// <summary>
    /// The normalized reading. Present on success, and on an <see cref="LegajoExtractionErrors.InvalidOutput"/>
    /// failure (with its violations) so the failed run can record why.
    /// </summary>
    public LegajoExtractionReading? Reading { get; init; }

    /// <summary>Exact model id sent.</summary>
    public required string Model { get; init; }

    /// <summary>Document or Images.</summary>
    public required string ReadMode { get; init; }

    /// <summary>HTTP status of the last response, or null.</summary>
    public int? HttpStatus { get; init; }

    /// <summary>Input tokens billed.</summary>
    public int InputTokens { get; init; }

    /// <summary>Output tokens billed.</summary>
    public int OutputTokens { get; init; }

    /// <summary>SHA-256 hex of the request body, empty when none was built.</summary>
    public string InputHash { get; init; } = string.Empty;

    /// <summary>SHA-256 hex of the last response body, empty when none arrived.</summary>
    public string OutputHash { get; init; } = string.Empty;

    /// <summary>Wall-clock of the pass including retries.</summary>
    public int DurationMs { get; init; }
}
