// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// The compare-and-set writes on [dbo].[Documents].[ProcessingStatus] that a background analysis
/// needs, plus the read its stale-Processing sweep runs. Kept apart from
/// <see cref="IDocumentRepository"/> on purpose: every write here is CONDITIONAL on the status the
/// caller last saw, where IDocumentRepository.UpdateStatusAsync is unconditional — and the synchronous
/// POST /api/analyses path keeps using that one unchanged.
/// </summary>
public interface IAnalysisRunStateRepository
{
    /// <summary>
    /// Moves the document to "Processing" (ProcessedAt = NULL) only if it still holds
    /// <paramref name="expectedStatus"/> and <paramref name="expectedProcessedAt"/> — the values the
    /// caller validated. True when this call won; false when anything changed the row meanwhile
    /// (another trigger claimed it, a run finished). Two concurrent background triggers can therefore
    /// never both start a job for one document.
    /// </summary>
    Task<bool> TryBeginProcessingAsync(
        Guid documentId, string tenantId, string expectedStatus, DateTime? expectedProcessedAt, CancellationToken ct);

    /// <summary>
    /// Puts back the status and timestamp a document had before <see cref="TryBeginProcessingAsync"/>,
    /// only while it is still "Processing". For a job that was claimed but could not be queued.
    /// </summary>
    Task<bool> TryRestoreAsync(
        Guid documentId, string tenantId, string status, DateTime? processedAt, CancellationToken ct);

    /// <summary>
    /// Moves the document to "Failed" (ProcessedAt = <paramref name="failedAt"/>) only while it is still
    /// "Processing", so a run that completed a moment ago is never clobbered. True when this call moved it.
    /// </summary>
    Task<bool> TryFailProcessingAsync(Guid documentId, string tenantId, DateTime failedAt, CancellationToken ct);

    /// <summary>
    /// Every non-deleted document currently in "Processing", ACROSS ALL TENANTS.
    /// <para>
    /// The one deliberate exception to tenant scoping in this interface: it serves the stale-Processing
    /// sweep, a system job that belongs to no request and no tenant — the same shape, and the same
    /// reason, as LegajoDocumentRepository.GetRecoverableAsync. Each row carries its TenantId, and every
    /// write that follows is scoped by (TenantId, DocumentId). Read-only and projected: no file name, no
    /// content, no vendor payload.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<ProcessingDocument>> GetProcessingDocumentsAsync(CancellationToken ct);
}

/// <summary>A document seen in "Processing" by the stale sweep.</summary>
/// <param name="DocumentId">The document.</param>
/// <param name="TenantId">Its tenant — scopes every write the sweep makes.</param>
/// <param name="EntityId">Its entity, for the failure notification's copy.</param>
/// <param name="UploadedBy">
/// Who uploaded it: the addressee of the failure notification, because no column records who
/// triggered the run.
/// </param>
public sealed record ProcessingDocument(Guid DocumentId, string TenantId, Guid EntityId, Guid UploadedBy);
