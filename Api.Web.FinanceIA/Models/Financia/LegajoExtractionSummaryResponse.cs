// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// LegajoDocumentResponse.CurrentExtraction (M10 Phase 2b): the document's current extraction at a
/// glance, computed on the EFFECTIVE values (corrections applied). The detail is
/// GET /api/documents/{id}/extraction.
/// </summary>
public sealed record LegajoExtractionSummaryResponse
{
    /// <summary>The current run.</summary>
    public required Guid ExtractionId { get; init; }

    /// <summary>The output shape version.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>Document or Images: how the kept pass read the file.</summary>
    public required string ReadMode { get; init; }

    /// <summary>The run's mean field confidence, 0..1.</summary>
    public decimal? Confidence { get; init; }

    /// <summary>True when no integrity check fails on the effective values.</summary>
    public required bool ChecksPassed { get; init; }

    /// <summary>How many checks fail on the effective values.</summary>
    public required int FailedChecks { get; init; }

    /// <summary>Required fields with no value.</summary>
    public required int MissingCount { get; init; }

    /// <summary>Fields flagged for low confidence or a failed check.</summary>
    public required int FlaggedCount { get; init; }

    /// <summary>Corrections recorded against the run.</summary>
    public required int CorrectionsCount { get; init; }

    /// <summary>When an analyst approved it; null until then (and again after a later correction).</summary>
    public DateTime? ReviewedAt { get; init; }

    /// <summary>
    /// True when the approval was an override (a check still failed or a required field was missing), false
    /// for a plain approval; null while not approved, and for a run approved before DDL 071 recorded it.
    /// </summary>
    public bool? ReviewedWithOverride { get; init; }

    /// <summary>The reason given on approval (mandatory for an override), or null.</summary>
    public string? ReviewReason { get; init; }
}
