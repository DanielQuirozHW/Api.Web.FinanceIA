// Project : Api.Web.FinanceIA
// Created : 2026-11-03

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// The reads behind GET /api/analyses/runs. Every method is scoped to <c>tenantId</c> and read-only.
///
/// <para>
/// The three document lists share one filter: business BALANCE documents only — not deleted, not in a
/// persona humana legajo (<c>PersonLegajoId</c> and <c>LegajoStatus</c> both null; a legajo document's
/// pipeline state lives in <c>LegajoStatus</c>, never in <c>ProcessingStatus</c>), and of type
/// <see cref="DocumentTypes.Balance"/>. Each is newest first and returns at most <c>take</c> rows.
/// </para>
/// </summary>
public interface IAnalysisRunListRepository
{
    /// <summary>Documents in "Processing" (queued or running), newest upload first.</summary>
    Task<IReadOnlyList<AnalysisRunDocumentProjection>> GetRunningAsync(
        string tenantId, int take, CancellationToken ct);

    /// <summary>Documents in "Failed" whose failure (<c>ProcessedAt</c>) is at or after <paramref name="sinceUtc"/>.</summary>
    Task<IReadOnlyList<AnalysisRunDocumentProjection>> GetFailedAsync(
        string tenantId, DateTime sinceUtc, int take, CancellationToken ct);

    /// <summary>
    /// Documents in "Completed" that finished at or after <paramref name="sinceUtc"/>, each with the
    /// <c>ResultId</c> of its latest balance analysis (null when none exists — a data inconsistency).
    /// </summary>
    Task<IReadOnlyList<AnalysisRunDocumentProjection>> GetCompletedAsync(
        string tenantId, DateTime sinceUtc, int take, CancellationToken ct);

    /// <summary>
    /// Failed extraction attempts per balance FILE: <c>ExtractionAttempts</c> summed across the tenant's
    /// non-deleted documents sharing each hash — the rule of
    /// <see cref="IDocumentRepository.SumExtractionAttemptsByHashAsync"/>, for many hashes at once. A blank
    /// hash is never matched; a hash with no documents is absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> SumExtractionAttemptsByHashesAsync(
        IReadOnlyCollection<string> fileHashes, string tenantId, CancellationToken ct);

    /// <summary>
    /// The <c>Analysis.Failed</c> and <c>Analysis.AutoRetryFinished</c> audit rows of
    /// <paramref name="documentIds"/> written at or after <paramref name="sinceUtc"/>, newest first, each
    /// carrying its <see cref="AnalysisRunFailureAuditProjection.Action"/>.
    /// </summary>
    Task<IReadOnlyList<AnalysisRunFailureAuditProjection>> GetFailureAuditsAsync(
        IReadOnlyCollection<Guid> documentIds, string tenantId, DateTime sinceUtc, CancellationToken ct);

    /// <summary>Display names of the tenant's users among <paramref name="userIds"/>; unknown ids are absent.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetUserDisplayNamesAsync(
        IReadOnlyCollection<Guid> userIds, string tenantId, CancellationToken ct);
}
