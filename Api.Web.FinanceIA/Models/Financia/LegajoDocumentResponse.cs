// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One document of GET /api/person-legajos/{legajoId}/documents. Never carries the holder CUIT the
/// classification read — only whether it matches the legajo's person, so a third party's CUIT on a
/// Blocked document is never returned.
/// </summary>
public sealed record LegajoDocumentResponse
{
    /// <summary>The document.</summary>
    public required Guid DocumentId { get; init; }

    /// <summary>The declared (or analyst-confirmed) type.</summary>
    public required string DocumentType { get; init; }

    /// <summary>As uploaded.</summary>
    public required string OriginalFileName { get; init; }

    /// <summary>UTC.</summary>
    public required DateTime UploadedAt { get; init; }

    /// <summary>Bytes.</summary>
    public required long FileSizeBytes { get; init; }

    /// <summary>Null when the PDF could not be read.</summary>
    public int? PageCount { get; init; }

    /// <summary>Native / Scanned / Mixed, or null.</summary>
    public string? PdfType { get; init; }

    /// <summary>The pipeline status (LegajoStatuses).</summary>
    public string? LegajoStatus { get; init; }

    /// <summary>YYYY-MM-DD, or null.</summary>
    public DateOnly? PeriodEnd { get; init; }

    /// <summary>Certificacion / Compilacion / Ninguno, or null.</summary>
    public string? EngagementType { get; init; }

    /// <summary>True/false once a holder CUIT was read; null when none was.</summary>
    public bool? HolderCuitMatches { get; init; }

    /// <summary>Page-verdict counts.</summary>
    public required LegajoPageQualityResponse PageQuality { get; init; }

    /// <summary>The current classification, or null before one exists.</summary>
    public LegajoClassificationResponse? Classification { get; init; }

    /// <summary>
    /// The current extraction on its effective values (Phase 2b), or null when there is none — and always
    /// null for a Blocked document, whose extraction read another person's paperwork.
    /// </summary>
    public LegajoExtractionSummaryResponse? CurrentExtraction { get; init; }

    /// <summary>
    /// Why the pipeline failed: a short machine code (e.g. <c>rate_limited</c>, <c>extraction_timeout</c>),
    /// only when <see cref="LegajoStatus"/> is Failed — null otherwise. Always one of the legajo
    /// vocabulary's codes, or <c>unexpected</c>; never a message, a value or personal data.
    /// </summary>
    public string? LastErrorCode { get; init; }
}
