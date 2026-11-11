// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-11-08

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Response DTO for a single document.
/// Returned by GET /api/documents, GET /api/documents/{id} and POST /api/documents/upload.
/// </summary>
public sealed class DocumentResponse
{
    public Guid DocumentId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public Guid UploadedBy { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public int? PageCount { get; set; }
    public string MimeType { get; set; } = string.Empty;
    public string FileHashSHA256 { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public int? FiscalYear { get; set; }
    public string? PdfType { get; set; }
    public string ProcessingStatus { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string? Source { get; set; }

    /// <summary>
    /// Durable reference to the source LUFE document (its URL) for LUFE imports; null
    /// otherwise. The Portal compares this exactly against a LUFE entry's URL.
    /// </summary>
    public string? SourceRef { get; set; }

    // ── Persona humana legajo (DDL 070) — additive; null for every company document ──

    /// <summary>The legajo the document belongs to, or null.</summary>
    public Guid? PersonLegajoId { get; set; }

    /// <summary>The document's pipeline status (see LegajoStatuses), or null.</summary>
    public string? LegajoStatus { get; set; }

    /// <summary>Period-close or measurement date the classification read, as YYYY-MM-DD, or null.</summary>
    public DateOnly? PeriodEnd { get; set; }

    /// <summary>Certificacion / Compilacion / Ninguno, or null.</summary>
    public string? EngagementType { get; set; }

    // ── Failure flags (Análisis en curso) — additive; null for every persona humana document ──
    // Filled by GET /api/documents, GET /api/documents/{id} and the idempotent 200 of POST
    // /api/documents/upload; null on every other response that carries this DTO.

    /// <summary>
    /// What made the document's latest balance run fail, for a document whose <see cref="ProcessingStatus"/>
    /// is <c>Failed</c>: the same closed vocabulary as GET /api/analyses/runs' <c>failureReason</c>
    /// (<c>no_financial_statements</c>, <c>unreadable_document</c>, <c>file_rejected</c>, <c>timeout</c>,
    /// <c>service_unavailable</c>, <c>reading_failed</c>, <c>interrupted</c>, <c>consent_required</c>,
    /// <c>other</c>). A failure that left no reason on the document — or only one written by an EARLIER failure —
    /// reads as <c>other</c>. Never the stored text.
    /// Null when the document is not Failed, or belongs to a persona humana legajo.
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// Whose move it is after the failure: <c>document</c> (upload another file) for
    /// <c>no_financial_statements</c>, <c>unreadable_document</c> and <c>file_rejected</c>; <c>ours</c> (retry,
    /// or tell support) for every other reason. Null exactly when <see cref="FailureReason"/> is null.
    /// </summary>
    public string? CauseGroup { get; set; }

    /// <summary>
    /// Extraction attempts this FILE has left: max(0, Billing:MaxExtractionAttemptsPerDocument − attempts spent
    /// on every non-deleted document of the tenant with the same content hash) — the arithmetic of GET
    /// /api/analyses/runs. Zero means POST /api/analyses will refuse it (<c>extraction_budget_exhausted</c>).
    /// Null for a Completed document, a persona humana document, when the budget is off (≤ 0), when the hash is
    /// unknown, or when the attempts could not be read.
    /// </summary>
    public int? AttemptsRemaining { get; set; }
}
