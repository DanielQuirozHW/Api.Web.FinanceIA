// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-05-04

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[Documents].
/// Represents a financial document uploaded by a user for a specific entity.
/// </summary>
public sealed class Document
{
    /// <summary>Primary key.</summary>
    public Guid DocumentId { get; set; }

    /// <summary>Tenant that owns this document. Firebase tenant slug (e.g. SgrOneClickSandboxFB2).</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Entity (PyME) this document belongs to.</summary>
    public Guid EntityId { get; set; }

    /// <summary>User who uploaded the document.</summary>
    public Guid UploadedBy { get; set; }

    /// <summary>Original file name as provided by the uploader.</summary>
    public string OriginalFileName { get; set; } = string.Empty;

    /// <summary>File name used for internal storage (sanitized/unique).</summary>
    public string StoredFileName { get; set; } = string.Empty;

    /// <summary>Full storage path (e.g., Azure Blob container path).</summary>
    public string StoragePath { get; set; } = string.Empty;

    /// <summary>File size in bytes.</summary>
    public long FileSizeBytes { get; set; }

    /// <summary>Number of pages in the document. Null for non-paged file types.</summary>
    public int? PageCount { get; set; }

    /// <summary>MIME type of the uploaded file.</summary>
    public string MimeType { get; set; } = string.Empty;

    /// <summary>SHA-256 hash of the file content for integrity verification.</summary>
    public string FileHashSHA256 { get; set; } = string.Empty;

    /// <summary>Document type classifier (e.g., BalanceSheet, IncomeStatement).</summary>
    public string DocumentType { get; set; } = string.Empty;

    /// <summary>Fiscal year the document corresponds to. Null when not applicable.</summary>
    public int? FiscalYear { get; set; }

    /// <summary>PDF variant (e.g., Scanned, Digital). Null for non-PDF files.</summary>
    public string? PdfType { get; set; }

    /// <summary>Current processing status. Default: Pending.</summary>
    public string ProcessingStatus { get; set; } = "Pending";

    /// <summary>
    /// How many extraction attempts have failed for this document. Zero for a document that has
    /// never failed.
    ///
    /// <para>
    /// This exists because <see cref="ProcessingStatus"/> cannot count: re-triggering the same
    /// document inserts no new row, so the status flips to "Failed" and stays there, saying
    /// "failed at least once" and never how many times. The analysis quota only counts delivered
    /// analyses, so without this counter an unreadable balance could buy Claude extractions
    /// without limit. Read per FILE, not per row — see
    /// <c>IDocumentRepository.SumExtractionAttemptsByHashAsync</c>.
    /// </para>
    /// </summary>
    public int ExtractionAttempts { get; set; }

    /// <summary>
    /// UTC timestamp of the most recent failed extraction attempt. Null means the document has
    /// never failed — never "failed at the epoch".
    /// </summary>
    public DateTime? LastExtractionFailedAt { get; set; }

    /// <summary>
    /// Short sanitized reason code for the most recent failed extraction (e.g.
    /// "claude_api_error", an exception type name). Never an exception message and never any part
    /// of a Claude payload: this column must carry no PII and no document contents. Null while the
    /// document has never failed.
    /// </summary>
    public string? LastExtractionError { get; set; }

    /// <summary>UTC timestamp when the document was uploaded.</summary>
    public DateTime UploadedAt { get; set; }

    /// <summary>UTC timestamp when processing completed. Null while processing.</summary>
    public DateTime? ProcessedAt { get; set; }

    /// <summary>UTC timestamp after which the document may be purged per retention policy.</summary>
    public DateTime RetentionExpiresAt { get; set; }

    /// <summary>Soft-delete flag. When true the document is hidden from all queries.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>UTC timestamp when the document was soft-deleted. Null while active.</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>Origin of the document. "Lufe" for imports; null for direct uploads.</summary>
    public string? Source { get; set; }

    /// <summary>
    /// Durable reference to the source LUFE document this was imported from — its URL,
    /// the only LUFE field that distinguishes same-name/same-period siblings. Lets the
    /// Portal match an imported document to its exact LUFE listing entry instead of
    /// guessing by filename. Null for direct uploads and for rows imported before this
    /// column existed.
    /// </summary>
    public string? SourceRef { get; set; }

    // ── Persona humana legajo (DDL 070) ──────────────────────────────────────────
    // Every column below is NULL for a document that does not belong to a legajo, which is every
    // row that predates 070 and every row the legacy upload and the LUFE import write.

    /// <summary>
    /// The persona humana legajo this document was uploaded into, or null for a company document.
    /// FK_Documents_PersonLegajos exists in the database but is deliberately NOT modelled — see
    /// <c>DocumentConfiguration</c>.
    /// </summary>
    public Guid? PersonLegajoId { get; set; }

    /// <summary>
    /// Period-close or measurement date the classification read (e.g. 31/12 of a DDJJ's period, or
    /// the date a manifestación de bienes is measured at). Null until classified. Time is always
    /// midnight: the column is DATE.
    /// </summary>
    public DateTime? PeriodEnd { get; set; }

    /// <summary>One of <see cref="EngagementTypes.Known"/>, as the classification read it, or null.</summary>
    public string? EngagementType { get; set; }

    /// <summary>
    /// The document a rectificativa supersedes. Reserved: nothing sets it in Phase 2a.
    /// </summary>
    public Guid? SupersedesDocumentId { get; set; }

    /// <summary>
    /// The holder CUIT the classification read, in <c>XX-XXXXXXXX-X</c> form. PII, and possibly a
    /// THIRD PARTY's when the document is <see cref="LegajoStatuses.Blocked"/>: never returned by an
    /// endpoint (the read model exposes only whether it matches), never logged, never put in a prompt.
    /// </summary>
    public string? HolderCuit { get; set; }

    /// <summary>One of <see cref="LegajoStatuses.Known"/>; null for every non-legajo document.</summary>
    public string? LegajoStatus { get; set; }

    /// <summary>UTC time <see cref="LegajoStatus"/> last changed. The worker's stale-requeue reads it.</summary>
    public DateTime? LegajoStatusUpdatedAt { get; set; }

    /// <summary>
    /// Per-page text quality measured at upload by <c>PageTextQualityAnalyzer</c>:
    /// <c>[{page, chars, digits, suspiciousRatio, verdict}]</c>. Figures only — never page text.
    /// </summary>
    public string? PageQualityJson { get; set; }
}
