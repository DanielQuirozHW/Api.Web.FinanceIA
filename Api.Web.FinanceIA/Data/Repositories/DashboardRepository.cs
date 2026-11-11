// Project : Api.Web.FinanceIA
// Created : 2026-10-23

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IDashboardRepository"/>. Read-only: nothing here writes.
/// </summary>
public sealed partial class DashboardRepository : IDashboardRepository
{
    // Analyses.FinalRiskLevel / AnalysisResults.AiSuggestedLevel values, highest risk first. Anything
    // else — null included — sorts after Low.
    private const string Critical = "Critical";
    private const string High     = "High";
    private const string Medium   = "Medium";
    private const string Low      = "Low";

    private readonly FinanciaDbContext _dbContext;

    /// <summary>
    /// Initializes a new instance of <see cref="DashboardRepository"/>.
    /// </summary>
    public DashboardRepository(FinanciaDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<DashboardAnalysisRow> Items, int TotalCount)> GetMyPendingAsync(
        string tenantId, Guid userId, int take, CancellationToken ct)
    {
        var total = await MyPendingSource(tenantId, userId).CountAsync(ct).ConfigureAwait(false);

        var items = await MyPendingQuery(tenantId, userId)
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return (await WithBalanceAsync(tenantId, items, ct).ConfigureAwait(false), total);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardAnalysisRow>> GetMyPendingDegradedAsync(
        string tenantId, Guid userId, CancellationToken ct)
    {
        var degraded = nameof(BcraEnrichmentOutcome.Degraded);

        var items = await MyPendingSource(tenantId, userId)
            .Where(x => x.EnrichmentStatus == degraded)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return await WithBalanceAsync(tenantId, items, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<DashboardAnalysisRow> Items, int TotalCount)> GetReturnedAsync(
        string tenantId, Guid userId, int take, CancellationToken ct)
    {
        var total = await ReturnedSource(tenantId, userId).CountAsync(ct).ConfigureAwait(false);

        var items = await ReturnedQuery(tenantId, userId)
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (items.Count == 0)
            return (items, total);

        return (await WithReviewNotesAsync(tenantId, items, ct).ConfigureAwait(false), total);
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<DashboardAnalysisRow> Items, int TotalCount)> GetAwaitingMySignatureAsync(
        string tenantId, Guid userId, int take, CancellationToken ct)
    {
        var source = Rows(tenantId, a => a.Status == AnalysisStatuses.PreApproved
                                      && a.PreApprovedBy == userId);

        var total = await source.CountAsync(ct).ConfigureAwait(false);

        var items = await source
            .OrderBy(x => x.PreApprovedAt)
            .ThenBy(x => x.ResultId)
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return (items, total);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardAnalysisRow>> GetMyRecentAsync(
        string tenantId, Guid userId, int take, CancellationToken ct)
    {
        return await Rows(tenantId, a => a.RequestedBy == userId)
            .OrderByDescending(x => x.CreatedAt)
            .ThenBy(x => x.ResultId)
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardAnalysisRow>> GetPreApprovedAsync(string tenantId, CancellationToken ct)
    {
        return await Rows(tenantId, a => a.Status == AnalysisStatuses.PreApproved)
            .OrderBy(x => x.PreApprovedAt)
            .ThenBy(x => x.ResultId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<Guid>> GetAnalysisIdsApprovedByAsync(
        string tenantId, Guid userId, IReadOnlyCollection<Guid> analysisIds, CancellationToken ct)
    {
        if (analysisIds.Count == 0)
            return new HashSet<Guid>();

        var ids = analysisIds.Distinct().ToList();

        var voted = await _dbContext.AnalysisApprovalVotes
            .AsNoTracking()
            .Where(v => v.TenantId == tenantId
                     && v.UserId   == userId
                     && v.Vote     == AnalysisVoteValues.Approved
                     && ids.Contains(v.AnalysisId))
            .Select(v => v.AnalysisId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return voted.ToHashSet();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, string>> GetSolicitanteFiltersAsync(
        string tenantId, IReadOnlyCollection<Guid> analysisIds, CancellationToken ct)
    {
        if (analysisIds.Count == 0)
            return new Dictionary<Guid, string>();

        var ids = analysisIds.Distinct().ToList();

        // Only the solicitante rows, and only the two columns that decide the suggestion plus the
        // ordering key. The earliest one per analysis is picked in memory — the same row
        // PersonAnalysisRepository.GetParticipantsAsync puts first and the detail reads.
        var rows = await _dbContext.AnalysisParticipants
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId
                     && ids.Contains(p.AnalysisId)
                     && p.Role == ParticipantRoles.Solicitante)
            .Select(p => new { p.AnalysisId, p.CreatedAt, p.FiltersJson })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .GroupBy(p => p.AnalysisId)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.CreatedAt).First().FiltersJson);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, string>> GetDocumentStatusesAsync(
        string tenantId, IReadOnlyCollection<Guid> documentIds, CancellationToken ct)
    {
        if (documentIds.Count == 0)
            return new Dictionary<Guid, string>();

        var ids = documentIds.Distinct().ToList();

        return await _dbContext.Documents
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId && ids.Contains(d.DocumentId))
            .Select(d => new { d.DocumentId, d.ProcessingStatus })
            .ToDictionaryAsync(d => d.DocumentId, d => d.ProcessingStatus, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardVoteRow>> GetVotesByUserSinceAsync(
        string tenantId, Guid userId, DateTime sinceUtc, CancellationToken ct)
    {
        return await _dbContext.AnalysisApprovalVotes
            .AsNoTracking()
            .Where(v => v.TenantId == tenantId && v.UserId == userId && v.VotedAt >= sinceUtc)
            .Select(v => new DashboardVoteRow(v.AnalysisId, v.Vote, v.VotedAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardAnalysisRow>> GetReviewedByUserSinceAsync(
        string tenantId, Guid userId, DateTime sinceUtc, CancellationToken ct)
    {
        return await Rows(tenantId, a => a.ReviewedBy == userId && a.ReviewedAt >= sinceUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DashboardAnalysisRow>> GetByAnalysisIdsAsync(
        string tenantId, IReadOnlyCollection<Guid> analysisIds, CancellationToken ct)
    {
        if (analysisIds.Count == 0)
            return [];

        var ids = analysisIds.Distinct().ToList();

        return await Rows(tenantId, a => ids.Contains(a.AnalysisId))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The tenant's analyses matching <paramref name="analysisFilter"/>, inner-joined to their result
    /// and projected. Both sides carry the tenant predicate.
    /// </summary>
    internal IQueryable<DashboardAnalysisRow> Rows(
        string tenantId, System.Linq.Expressions.Expression<Func<Analysis, bool>> analysisFilter)
    {
        var analyses = _dbContext.Analyses
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId)
            .Where(analysisFilter);

        var results = _dbContext.AnalysisResults
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId);

        return analyses.Join(
            results,
            a => a.AnalysisId,
            r => r.AnalysisId,
            (a, r) => new DashboardAnalysisRow
            {
                ResultId            = r.ResultId,
                AnalysisId          = a.AnalysisId,
                AnalysisKind        = a.AnalysisKind,
                EntityId            = a.EntityId,
                DocumentId          = a.DocumentId,
                RequestedBy         = a.RequestedBy,
                RequestedAmount     = a.RequestedAmount,
                Status              = a.Status,
                CreatedAt           = a.CreatedAt,
                PreApprovedBy       = a.PreApprovedBy,
                PreApprovedAt       = a.PreApprovedAt,
                PreApprovedAmount   = a.PreApprovedAmount,
                PreApprovedCurrency = a.PreApprovedCurrency,
                ReviewedBy          = a.ReviewedBy,
                ReviewedAt          = a.ReviewedAt,
                DecisionAmount      = a.DecisionAmount,
                DecisionCurrency    = a.DecisionCurrency,
                GeneratedAt         = r.GeneratedAt,
                AiSuggestedScore    = r.AiSuggestedScore,
                AiSuggestedLevel    = r.AiSuggestedLevel,
                EnrichmentStatus    = r.EnrichmentStatus
            });
    }

    /// <summary>
    /// Fills <see cref="DashboardAnalysisRow.HasBalance"/> for <paramref name="rows"/> with one query.
    /// Uses the detail's own test — not null or whitespace — evaluated in the database, so the blob
    /// itself never crosses the wire.
    /// </summary>
    private async Task<IReadOnlyList<DashboardAnalysisRow>> WithBalanceAsync(
        string tenantId, List<DashboardAnalysisRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
            return rows;

        var resultIds = rows.Select(x => x.ResultId).ToList();

        var withBalance = await BalancePresenceQuery(tenantId, resultIds)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var present = withBalance.ToHashSet();
        return rows.Select(x => x with { HasBalance = present.Contains(x.ResultId) }).ToList();
    }

    /// <summary>
    /// Fills <see cref="DashboardAnalysisRow.ReviewNotes"/> for <paramref name="rows"/> with a second,
    /// page-sized read rather than a column on every dashboard projection: ReviewNotes is free text and
    /// only the sections that show it pay for it.
    /// </summary>
    private async Task<IReadOnlyList<DashboardAnalysisRow>> WithReviewNotesAsync(
        string tenantId, IReadOnlyList<DashboardAnalysisRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
            return rows;

        var ids   = rows.Select(x => x.AnalysisId).Distinct().ToList();
        var notes = await _dbContext.Analyses
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && ids.Contains(a.AnalysisId))
            .Select(a => new { a.AnalysisId, a.ReviewNotes })
            .ToDictionaryAsync(a => a.AnalysisId, a => a.ReviewNotes, ct)
            .ConfigureAwait(false);

        return rows.Select(x => x with { ReviewNotes = notes.GetValueOrDefault(x.AnalysisId) }).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, DashboardScoreReliabilitySource>> GetScoreReliabilitySourcesAsync(
        string tenantId, IReadOnlyCollection<Guid> resultIds, CancellationToken ct)
    {
        if (resultIds.Count == 0)
            return new Dictionary<Guid, DashboardScoreReliabilitySource>();

        var rows = await ScoreReliabilitySourceQuery(tenantId, resultIds)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ToDictionary(r => r.ResultId);
    }

    // ── Query shapes — internal so the SQL Server translation can be asserted with ToQueryString ──

    /// <summary>
    /// The score-reliability columns of <paramref name="resultIds"/>: exactly four, the breakdown blob among
    /// them — which is why this is its own page-sized query and never part of <see cref="Rows"/>.
    /// </summary>
    internal IQueryable<DashboardScoreReliabilitySource> ScoreReliabilitySourceQuery(
        string tenantId, IReadOnlyCollection<Guid> resultIds)
        => from r in _dbContext.AnalysisResults.AsNoTracking()
           join a in _dbContext.Analyses.AsNoTracking() on r.AnalysisId equals a.AnalysisId
           where r.TenantId == tenantId && a.TenantId == tenantId && resultIds.Contains(r.ResultId)
           select new DashboardScoreReliabilitySource(r.ResultId, r.RescoredAt, a.FiscalYear, r.ScoreBreakdownJson);

    /// <summary>The user's undecided PendingReview analyses, unordered: the filter the COUNT runs on.</summary>
    private IQueryable<DashboardAnalysisRow> MyPendingSource(string tenantId, Guid userId)
        => Rows(tenantId, a => a.Status == AnalysisStatuses.PendingReview
                            && a.RequestedBy == userId
                            && a.ReviewedBy == null);

    /// <summary>
    /// <see cref="MyPendingSource"/> in the section's order: risk level descending as a CASE, then
    /// oldest first, then ResultId so equal rows page deterministically.
    /// </summary>
    internal IQueryable<DashboardAnalysisRow> MyPendingQuery(string tenantId, Guid userId)
        => MyPendingSource(tenantId, userId)
            .OrderBy(x => x.AiSuggestedLevel == Critical ? 0
                        : x.AiSuggestedLevel == High     ? 1
                        : x.AiSuggestedLevel == Medium   ? 2
                        : x.AiSuggestedLevel == Low      ? 3
                        :                                  4)
            .ThenBy(x => x.GeneratedAt)
            .ThenBy(x => x.ResultId);

    /// <summary>Returned-for-rework analyses the user requested or pre-approved, unordered.</summary>
    private IQueryable<DashboardAnalysisRow> ReturnedSource(string tenantId, Guid userId)
        => Rows(tenantId, a => a.Status == AnalysisStatuses.PendingReview
                            && a.ReviewedBy != null
                            && (a.RequestedBy == userId || a.PreApprovedBy == userId));

    /// <summary><see cref="ReturnedSource"/>, newest return first.</summary>
    internal IQueryable<DashboardAnalysisRow> ReturnedQuery(string tenantId, Guid userId)
        => ReturnedSource(tenantId, userId)
            .OrderByDescending(x => x.ReviewedAt)
            .ThenBy(x => x.ResultId);

    /// <summary>The results among <paramref name="resultIds"/> whose BalanceJson is not null or whitespace.</summary>
    internal IQueryable<Guid> BalancePresenceQuery(string tenantId, IReadOnlyCollection<Guid> resultIds)
        => _dbContext.AnalysisResults
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId
                     && resultIds.Contains(r.ResultId)
                     && !string.IsNullOrWhiteSpace(r.BalanceJson))
            .Select(r => r.ResultId);
}
