// Project : Api.Web.FinanceIA
// Created : 2026-10-25

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// The input of the annual review rule (<c>AnnualReviewRule</c>), in one place: CreditLineRepository reads
/// it for PYME_REVIEW_DUE and DashboardRepository for the portfolio's annualReview, so the alert and the
/// dashboard select the same PyMEs and count from the same dates.
/// <para>
/// Extracted verbatim from <c>CreditLineRepository.BuildLatestApprovedAnalyses</c>.
/// </para>
/// </summary>
internal static class PymeReviewQueries
{
    private const string ActiveLine = "Active";

    /// <summary>
    /// The entities of the tenant's Active cupos, each LEFT JOINED to its latest approval date —
    /// MAX(ReviewedAt ?? CreatedAt) over its 'Approved' analyses — and back to the analysis (or, on an
    /// exact tie, analyses) carrying that date, as an unexecuted query. Every table is filtered to the
    /// tenant before the join, so a key of another tenant joins to nothing.
    /// </summary>
    public static IQueryable<PymeReviewSource> LatestApprovedAnalysesOfActiveEntities(FinanciaDbContext dbContext, string tenantId)
    {
        var entityIds = dbContext.CreditLines
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Status == ActiveLine)
            .Select(c => c.EntityId)
            .Distinct();
        var approved  = dbContext.Analyses
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.Status == AnalysisStatuses.Approved);
        var latest = approved
            .GroupBy(a => a.EntityId)
            .Select(g => new { EntityId = g.Key, ApprovedAt = g.Max(a => a.ReviewedAt ?? a.CreatedAt) });

        return from entityId in entityIds
               join l in latest on entityId equals l.EntityId into lj
               from l in lj.DefaultIfEmpty()
               join a in approved
                   on new { EntityId = entityId, ApprovedAt = l == null ? (DateTime?)null : l.ApprovedAt }
                   equals new { a.EntityId, ApprovedAt = (DateTime?)(a.ReviewedAt ?? a.CreatedAt) } into aj
               from a in aj.DefaultIfEmpty()
               select new PymeReviewSource
               {
                   EntityId   = entityId,
                   AnalysisId = a == null ? null : a.AnalysisId,
                   ApprovedAt = a == null ? null : (DateTime?)(a.ReviewedAt ?? a.CreatedAt)
               };
    }

    /// <summary>
    /// Runs <see cref="LatestApprovedAnalysesOfActiveEntities"/> and keeps one row per PyME. Two approvals
    /// of one PyME at the very same instant join back as two rows; the same one is kept every time.
    /// </summary>
    public static async Task<IReadOnlyList<PymeReviewSource>> ReadAsync(
        FinanciaDbContext dbContext, string tenantId, CancellationToken ct)
    {
        var rows = await LatestApprovedAnalysesOfActiveEntities(dbContext, tenantId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => r.EntityId)
            .Select(g => g.OrderByDescending(r => r.AnalysisId).First())
            .ToList();
    }
}
