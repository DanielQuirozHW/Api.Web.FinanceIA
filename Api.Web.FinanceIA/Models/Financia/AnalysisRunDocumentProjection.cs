// Project : Api.Web.FinanceIA
// Created : 2026-11-03

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One balance document as GET /api/analyses/runs reads it: the document's own columns plus its
/// entity's name and legajo link. A REPOSITORY projection, not a wire contract —
/// <see cref="LastExtractionError"/> in particular is stored text and must never reach a response.
/// </summary>
public sealed class AnalysisRunDocumentProjection
{
    public Guid DocumentId { get; init; }

    public Guid EntityId { get; init; }

    /// <summary>The entity's business name; null when the entity row is missing.</summary>
    public string? EntityName { get; init; }

    /// <summary>The entity's persona humana legajo, when it has one (the consent check reads it).</summary>
    public Guid? EntityPersonLegajoId { get; init; }

    public string FileName { get; init; } = string.Empty;

    public DateTime UploadedAt { get; init; }

    /// <summary>For a Failed document, when it failed; for a Completed one, when it finished.</summary>
    public DateTime? ProcessedAt { get; init; }

    public string FileHashSha256 { get; init; } = string.Empty;

    public string? LastExtractionError { get; init; }

    public DateTime? LastExtractionFailedAt { get; init; }

    /// <summary>Documents.FiscalYear: the year the analyst gave at upload, or null.</summary>
    public int? FiscalYear { get; init; }

    /// <summary>Completed documents only: the result of the document's latest balance analysis.</summary>
    public Guid? ResultId { get; init; }
}
