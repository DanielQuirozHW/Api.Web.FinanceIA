// Project : Api.Web.FinanceIA
// Created : 2026-11-06

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// The reads behind GET /api/analyses/{id}/previous-comparison. A repository of its own, rather than
/// two more methods on <see cref="IAnalysisRepository"/>, so the hand-rolled stubs of that interface
/// across the test suite are left untouched.
/// </summary>
public interface IAnalysisComparisonRepository
{
    /// <summary>
    /// The analysis behind one result, tenant-scoped on both tables; null when the result is not this
    /// tenant's (or its analysis row is missing).
    /// </summary>
    Task<AnalysisComparisonSnapshot?> GetSnapshotByResultIdAsync(
        Guid resultId, string tenantId, CancellationToken ct);

    /// <summary>
    /// The analysis an analysis is compared against: same tenant, same entity, same kind; decided
    /// (Approved, PreApproved or Rejected); created strictly before <paramref name="currentCreatedAt"/>;
    /// not <paramref name="currentAnalysisId"/>; for a company, with a scored result and a balance that was
    /// read (one of assets, equity, net_revenue, net_profit); for a persona humana, with a stored evaluation
    /// (its Solicitante participant row). The latest by decision time
    /// (ReviewedAt, else PreApprovedAt, else CreatedAt), then CreatedAt, then AnalysisId. Null when none.
    /// Resolved at read time — <c>Analyses.ComparativeAnalysisId</c> is never read or written.
    /// </summary>
    Task<AnalysisComparisonSnapshot?> GetPreviousSnapshotAsync(
        string tenantId,
        Guid entityId,
        string analysisKind,
        Guid currentAnalysisId,
        DateTime currentCreatedAt,
        CancellationToken ct);

    /// <summary>
    /// A persona humana analysis's solicitante evaluation and terms, with its fiadores' ids, tenant-scoped;
    /// null when the analysis has no Solicitante row (no stored evaluation) in this tenant.
    /// </summary>
    Task<PersonComparisonFacts?> GetPersonFactsAsync(
        Guid analysisId, string tenantId, CancellationToken ct);
}
