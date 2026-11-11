// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using Api.Web.FinanceIA.Models.Nosis;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// What <see cref="INosisManagerReportParser.Parse"/> returns. <see cref="SubjectCuitDigits"/> is the
/// report's CUIT, carried ONLY so the caller can compare it with the entity's; it is never stored,
/// logged or returned to the client.
/// </summary>
public sealed record NosisReportParseResult
{
    public required NosisReportParseOutcome Outcome { get; init; }

    public NosisReportNotRecognizedReason? Reason { get; init; }

    /// <summary>The report, scrubbed; <c>FileSha256</c> and <c>ImportedAt</c> are the caller's to set.</summary>
    public NosisManagerReport? Report { get; init; }

    /// <summary>The report's own date (Argentina wall-clock, Kind Unspecified).</summary>
    public DateTime? ReportDate { get; init; }

    /// <summary>11 digits. For comparison only — see the class remarks.</summary>
    public string? SubjectCuitDigits { get; init; }

    public int PageCount { get; init; }

    /// <summary>How many kept strings the privacy guard dropped.</summary>
    public int DroppedStrings { get; init; }
}
