// Project : Api.Web.FinanceIA
// Created : 2026-10-23

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

// GET /api/dashboard/oversight. The analysis reads go through Rows — the #261 projection, tenant on both
// tables, no blob — and cap in SQL wherever the total does not depend on an in-memory filter.
public sealed partial class DashboardRepository
{
    /// <inheritdoc />
    public async Task<(IReadOnlyList<DashboardAnalysisRow> Items, int TotalCount)> GetTeamQueueAsync(
        string tenantId, int take, CancellationToken ct)
    {
        var total = await TeamQueueSource(tenantId).CountAsync(ct).ConfigureAwait(false);

        var items = await TeamQueueQuery(tenantId)
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return (items, total);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardRequesterQueueRow>> GetTeamQueueByRequesterAsync(
        string tenantId, CancellationToken ct)
        => await TeamQueueByRequesterQuery(tenantId).ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<int> CountPreApprovedAsync(string tenantId, CancellationToken ct)
        => await Rows(tenantId, a => a.Status == AnalysisStatuses.PreApproved).CountAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardAnalysisRow>> GetAgainstSuggestionCandidatesAsync(
        string tenantId, DateTime sinceUtc, CancellationToken ct)
        => await AgainstSuggestionCandidatesQuery(tenantId, sinceUtc).ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardAnalysisRow>> GetRecentDecisionsAsync(
        string tenantId, int take, CancellationToken ct)
    {
        var items = await RecentDecisionsQuery(tenantId)
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return await WithReviewNotesAsync(tenantId, items, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, string?>> GetReviewNotesAsync(
        string tenantId, IReadOnlyCollection<Guid> analysisIds, CancellationToken ct)
    {
        if (analysisIds.Count == 0)
            return new Dictionary<Guid, string?>();

        var ids = analysisIds.Distinct().ToList();

        return await _dbContext.Analyses
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && ids.Contains(a.AnalysisId))
            .Select(a => new { a.AnalysisId, a.ReviewNotes })
            .ToDictionaryAsync(a => a.AnalysisId, a => a.ReviewNotes, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardRescoreRow>> GetRecentRescoresAsync(
        string tenantId, int take, CancellationToken ct)
        => await RecentRescoresQuery(tenantId).Take(take).ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardStatusCountRow>> GetCreatedByStatusAsync(
        string tenantId, DateTime startUtc, DateTime endUtc, CancellationToken ct)
        => await CreatedByStatusQuery(tenantId, startUtc, endUtc).ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardDecisionTimeRow>> GetDecisionTimesAsync(
        string tenantId, DateTime startUtc, DateTime endUtc, CancellationToken ct)
        => await DecisionTimesQuery(tenantId, startUtc, endUtc).ToListAsync(ct).ConfigureAwait(false);

    // ── Query shapes — internal so the SQL Server translation can be asserted with ToQueryString ──

    /// <summary>Every PendingReview analysis of the tenant, unordered: the filter the COUNT runs on.</summary>
    private IQueryable<DashboardAnalysisRow> TeamQueueSource(string tenantId)
        => Rows(tenantId, a => a.Status == AnalysisStatuses.PendingReview);

    /// <summary><see cref="TeamQueueSource"/>, oldest GeneratedAt first.</summary>
    internal IQueryable<DashboardAnalysisRow> TeamQueueQuery(string tenantId)
        => TeamQueueSource(tenantId)
            .OrderBy(x => x.GeneratedAt)
            .ThenBy(x => x.ResultId);

    /// <summary>The whole queue grouped by requester: undecided, returned, oldest GeneratedAt.</summary>
    internal IQueryable<DashboardRequesterQueueRow> TeamQueueByRequesterQuery(string tenantId)
        => TeamQueueSource(tenantId)
            .GroupBy(x => x.RequestedBy)
            .Select(g => new DashboardRequesterQueueRow(
                g.Key,
                g.Count(x => x.ReviewedBy == null),
                g.Count(x => x.ReviewedBy != null),
                g.Min(x => x.GeneratedAt)));

    /// <summary>
    /// Approvals since <paramref name="sinceUtc"/> that may be against the suggestion: a balance analysis
    /// at High or Critical (decided here), or any persona humana analysis — whose suggestion is derived
    /// from its solicitante's filters in C#, so it cannot be filtered in SQL. Newest first, uncapped.
    /// </summary>
    internal IQueryable<DashboardAnalysisRow> AgainstSuggestionCandidatesQuery(string tenantId, DateTime sinceUtc)
        => Rows(tenantId, a => a.Status == AnalysisStatuses.Approved && a.ReviewedAt >= sinceUtc)
            .Where(x => x.AnalysisKind == AnalysisKinds.PersonaHumana
                     || (x.AnalysisKind == AnalysisKinds.Balance
                         && (x.AiSuggestedLevel == High || x.AiSuggestedLevel == Critical)))
            .OrderByDescending(x => x.ReviewedAt)
            .ThenBy(x => x.ResultId);

    /// <summary>
    /// Analyses with a decision on record, newest ReviewedAt first. Only the statuses whose act the row
    /// PROVES: Approved, Rejected, and PendingReview with a reviewer (a return). A PreApproved row with a
    /// reviewer cannot say what the earlier act was, and is left out — as approvals' decidedByMe does.
    /// </summary>
    internal IQueryable<DashboardAnalysisRow> RecentDecisionsQuery(string tenantId)
        => Rows(tenantId, a => a.ReviewedBy != null
                            && a.ReviewedAt != null
                            && (a.Status == AnalysisStatuses.Approved
                             || a.Status == AnalysisStatuses.Rejected
                             || a.Status == AnalysisStatuses.PendingReview))
            .OrderByDescending(x => x.ReviewedAt)
            .ThenBy(x => x.ResultId);

    /// <summary>
    /// The tenant's re-scores, newest first, without PreviousValuesJson or SourcesJson, left-joined to their
    /// analysis for its EntityId and AnalysisKind. Both sides carry the tenant predicate, as in Rows: a
    /// re-score whose analysis is another tenant's still lists, with no entity. A persona re-evaluation's
    /// suggestions before and after are read out of PreviousValuesJson with JSON_VALUE, exactly as the history
    /// list (AnalysisRescoreRepository.GetHistoryAsync) reads them, so the blob is never selected.
    /// </summary>
    internal IQueryable<DashboardRescoreRow> RecentRescoresQuery(string tenantId)
    {
        var analyses = _dbContext.Analyses
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId);

        return _dbContext.AnalysisRescores
            .AsNoTracking()
            .Where(h => h.TenantId == tenantId)
            .GroupJoin(analyses, h => h.AnalysisId, a => a.AnalysisId, (h, matches) => new { h, matches })
            .SelectMany(x => x.matches.DefaultIfEmpty(), (x, a) => new { x.h, a })
            .OrderByDescending(x => x.h.RescoredAt)
            .ThenBy(x => x.h.RescoreId)
            .Select(x => new DashboardRescoreRow(
                x.h.RescoreId, x.h.AnalysisId, x.h.ResultId, x.h.RescoredAt, x.h.RescoredBy,
                x.h.PreviousScore, x.h.NewScore, x.h.PreviousLevel, x.h.NewLevel,
                x.h.PreviousEnrichmentStatus, x.h.NewEnrichmentStatus,
                x.a != null ? x.a.EntityId : (Guid?)null,
                x.a != null ? x.a.AnalysisKind : null,
                SqlJsonFunctions.JsonValue(x.h.PreviousValuesJson, PersonRescorePreviousValues.PreviousSuggestionPath),
                SqlJsonFunctions.JsonValue(x.h.PreviousValuesJson, PersonRescorePreviousValues.NewSuggestionPath)));
    }

    /// <summary>Analyses created in [<paramref name="startUtc"/>, <paramref name="endUtc"/>), by status now.</summary>
    internal IQueryable<DashboardStatusCountRow> CreatedByStatusQuery(string tenantId, DateTime startUtc, DateTime endUtc)
        => _dbContext.Analyses
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.CreatedAt >= startUtc && a.CreatedAt < endUtc)
            .GroupBy(a => a.Status)
            .Select(g => new DashboardStatusCountRow(g.Key, g.Count()));

    /// <summary>
    /// CreatedAt and ReviewedAt of the analyses created in the window that are now Approved or Rejected —
    /// two columns, so percentiles SQL Server cannot aggregate through EF are computed over a narrow list.
    /// </summary>
    internal IQueryable<DashboardDecisionTimeRow> DecisionTimesQuery(string tenantId, DateTime startUtc, DateTime endUtc)
        => _dbContext.Analyses
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId
                     && a.CreatedAt >= startUtc
                     && a.CreatedAt < endUtc
                     && a.ReviewedAt != null
                     && (a.Status == AnalysisStatuses.Approved || a.Status == AnalysisStatuses.Rejected))
            .Select(a => new DashboardDecisionTimeRow(a.CreatedAt, a.ReviewedAt!.Value));
}
