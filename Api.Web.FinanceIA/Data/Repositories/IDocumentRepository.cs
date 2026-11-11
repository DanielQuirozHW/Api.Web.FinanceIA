// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-05-04

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Persistence operations for <see cref="Document"/> records
/// in the per-tenant business database.
/// </summary>
public interface IDocumentRepository
{
    /// <summary>
    /// Returns the document with the specified id belonging to the given tenant,
    /// or null if not found.
    /// </summary>
    Task<Document?> GetByIdAsync(Guid documentId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns all documents belonging to the given tenant.
    /// </summary>
    Task<IReadOnlyList<Document>> GetAllByTenantAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns all documents belonging to the given entity within the given tenant.
    /// </summary>
    Task<IReadOnlyList<Document>> GetByEntityIdAsync(Guid entityId, string tenantId, CancellationToken ct);

    /// <summary>
    /// The newest non-deleted COMPANY document (no persona humana legajo) of <paramref name="entityId"/> in the
    /// tenant with the same <paramref name="documentType"/> and the same content hash, uploaded at or after
    /// <paramref name="uploadedSinceUtc"/>; null when there is none. The idempotency read of
    /// POST /api/documents/upload: a double submit of the same file must hand back the first row, not create a
    /// second one.
    ///
    /// <para>
    /// The hash and the type match ordinal-ignore-case. A null or blank hash never matches (it returns null
    /// without querying): a blank hash must never stand in for "same file".
    /// </para>
    /// </summary>
    Task<Document?> FindRecentByHashAsync(
        Guid entityId,
        string tenantId,
        string? fileHashSha256,
        string documentType,
        DateTime uploadedSinceUtc,
        CancellationToken ct);

    /// <summary>
    /// Persists a new document record and returns the saved document.
    /// </summary>
    Task<Document> CreateAsync(Document document, CancellationToken ct);

    /// <summary>
    /// Updates only the <c>ProcessingStatus</c> and <c>ProcessedAt</c> columns
    /// for the specified document using an EF Core bulk update (no entity load).
    /// </summary>
    Task UpdateStatusAsync(Guid documentId, string tenantId, string status, DateTime? processedAt, CancellationToken ct);

    /// <summary>
    /// Soft-deletes a document by setting <c>IsDeleted = true</c> and <c>DeletedAt</c>.
    /// Returns true if a row was updated, false if not found.
    /// </summary>
    Task<bool> SoftDeleteAsync(Guid documentId, string tenantId, DateTime deletedAt, CancellationToken ct);

    /// <summary>
    /// Sums <c>ExtractionAttempts</c> across every non-deleted document of the tenant whose
    /// <c>FileHashSHA256</c> matches, giving the failed-extraction attempts spent on this FILE
    /// rather than on one row.
    ///
    /// <para>
    /// Summing by content hash and not by document id is the point: re-uploading the identical
    /// bytes creates a fresh Pending row, and a per-row budget would reset with it. Returns 0 for
    /// a null or blank hash — a blank hash must never be allowed to match a whole tenant.
    /// </para>
    /// </summary>
    Task<int> SumExtractionAttemptsByHashAsync(string? fileHashSha256, string tenantId, CancellationToken ct);

    /// <summary>
    /// The <c>LastExtractionError</c> of the most recent failed extraction on this FILE: among the tenant's
    /// non-deleted documents whose <c>FileHashSHA256</c> matches, the one with the newest
    /// <c>LastExtractionFailedAt</c>. Null when none failed, or for a null or blank hash.
    ///
    /// <para>
    /// Same scope as <see cref="SumExtractionAttemptsByHashAsync"/>, so the reason the trigger reports for a
    /// spent budget comes from the same rows that spent it — typically an earlier upload of the same bytes,
    /// not the fresh row being triggered. The value is a stored CODE and never leaves the API as is.
    /// </para>
    /// </summary>
    Task<string?> GetLatestExtractionErrorByHashAsync(string? fileHashSha256, string tenantId, CancellationToken ct);

    /// <summary>
    /// Records <c>LastExtractionFailedAt</c> and <c>LastExtractionError</c> for the specified document and,
    /// when <paramref name="countsAsAttempt"/>, increments <c>ExtractionAttempts</c> by one.
    ///
    /// <para>
    /// Deliberately separate from <see cref="UpdateStatusAsync"/>: that method has callers which
    /// must NOT consume extraction budget (the "Processing" and "Completed" flips), so folding the
    /// increment into it would charge every status write.
    /// </para>
    /// </summary>
    /// <param name="error">
    /// A short sanitized reason CODE — never an exception message and never any part of a Claude
    /// payload. Truncated to the column's 200 characters by the caller-facing contract.
    /// </param>
    /// <param name="countsAsAttempt">
    /// Whether the failure spends one unit of the file's extraction budget. False for a failure this
    /// project or its provider caused (its own <c>max_tokens</c>, a 5xx, a 429, the network, its key, an
    /// interrupted run): the reason is still recorded, the budget is not charged — the same switch as
    /// <c>ILegajoDocumentRepository.FailAsync</c>.
    /// </param>
    Task RecordExtractionFailureAsync(
        Guid documentId, string tenantId, DateTime failedAt, string? error, bool countsAsAttempt, CancellationToken ct);
}
