// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-08-31

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IAnalysisRepository"/>.
/// </summary>
public sealed class AnalysisRepository : IAnalysisRepository
{
    // The two CK_Analyses_Status values the guarded confirm below compares against. Aliased from
    // AnalysisStatuses rather than spelled out, so the controller's routing and this UPDATE's guard
    // can never disagree about what "PreApproved" is spelled like.
    private const string PreApprovedStatus = AnalysisStatuses.PreApproved;
    private const string ApprovedStatus    = AnalysisStatuses.Approved;

    private readonly FinanciaDbContext _dbContext;
    private readonly ILogger<AnalysisRepository> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="AnalysisRepository"/>.
    /// </summary>
    public AnalysisRepository(FinanciaDbContext dbContext, ILogger<AnalysisRepository> logger)
    {
        _dbContext = dbContext;
        _logger    = logger;
    }

    /// <inheritdoc />
    public async Task<Analysis> CreateAsync(Analysis analysis, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        _dbContext.Analyses.Add(analysis);
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Analysis created: {AnalysisId} for document {DocumentId}",
            analysis.AnalysisId, analysis.DocumentId);

        return analysis;
    }

    /// <inheritdoc />
    public async Task<Analysis?> GetByIdAsync(Guid analysisId, CancellationToken ct)
    {
        return await _dbContext.Analyses
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.AnalysisId == analysisId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Analysis>> GetByIdsAsync(IEnumerable<Guid> analysisIds, CancellationToken ct)
    {
        var ids = analysisIds.ToList();
        if (ids.Count == 0) return [];

        return await _dbContext.Analyses
            .AsNoTracking()
            .Where(a => ids.Contains(a.AnalysisId))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Analysis?> GetLatestByDocumentIdAsync(Guid documentId, string tenantId, CancellationToken ct)
    {
        return await _dbContext.Analyses
            .AsNoTracking()
            .Where(a => a.DocumentId == documentId && a.TenantId == tenantId)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Analysis> UpdateAsync(Analysis analysis, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        _dbContext.Analyses.Update(analysis);
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Analysis updated: {AnalysisId} for tenant {TenantId} status={Status}",
            analysis.AnalysisId, analysis.TenantId, analysis.Status);

        return analysis;
    }

    /// <inheritdoc />
    public async Task<int> CountByTenantInPeriodAsync(
        string tenantId,
        DateTime periodStartUtc,
        DateTime periodEndUtc,
        CancellationToken ct)
    {
        // The half-open UTC window [start, end) arrives precomputed from ArgentineTime, the one
        // place that turns an Argentine calendar month into UTC boundaries. It is deliberately NOT
        // rebuilt here: the analysis count and the token count must describe the same period, and
        // that used to be guaranteed only by this comment matching UsageRepository's.

        // No Status predicate on purpose: a Rejected analysis still counts, as does the
        // re-analysis that replaced it. Note this counts COMPLETED analyses — a trigger that
        // failed before the row was written is free. See IAnalysisRepository for why.
        //
        // [dbo].[Analyses] has no (TenantId, CreatedAt) index — only IX_Analyses_TenantId_EntityId
        // and IX_Analyses_TenantId_Status — so this seeks on the tenant prefix and residual-filters
        // CreatedAt. That is fine at design-partner volume; if it ever is not, the fix is an index
        // DDL in the shape of scripts 044/045, not a change here.
        return await _dbContext.Analyses
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.CreatedAt >= periodStartUtc && a.CreatedAt < periodEndUtc)
            .CountAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, string>> GetLatestStatusByEntityIdsAsync(
        IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
    {
        var ids = entityIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, string>();

        // One indexed query (IX_Analyses_TenantId_EntityId) projecting scalars; the
        // latest-per-entity reduction runs in memory over the page's bounded result set.
        var rows = await _dbContext.Analyses
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && ids.Contains(a.EntityId))
            .Select(a => new { a.EntityId, a.Status, a.CreatedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .GroupBy(a => a.EntityId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(a => a.CreatedAt).First().Status);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, EntityLatestScoreProjection>> GetLatestScoreByEntityIdsAsync(
        IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var ids = entityIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, EntityLatestScoreProjection>();

        // One query projecting scalars (no nvarchar(max) blob is selected), both tables scoped by
        // TenantId — there is no global query filter. Only scored results take part, so a newer
        // failed or persona humana analysis cannot shadow the last real score. The latest-per-entity
        // reduction runs in memory over the page's bounded result set, as GetLatestStatusByEntityIdsAsync.
        // Then ONE more query reads the score breakdown of the WINNING rows only (page-sized, never per
        // row): selecting it here would read the blob of every scored analysis of every entity.
        var rows = await (
                from a in _dbContext.Analyses.AsNoTracking()
                join r in _dbContext.AnalysisResults.AsNoTracking() on a.AnalysisId equals r.AnalysisId
                where a.TenantId == tenantId
                   && r.TenantId == tenantId
                   && ids.Contains(a.EntityId)
                   && r.AiSuggestedScore != null
                select new
                {
                    a.EntityId,
                    a.AnalysisId,
                    a.CreatedAt,
                    a.AnalysisKind,
                    a.FiscalYear,
                    r.ResultId,
                    r.EnrichmentStatus,
                    r.GeneratedAt,
                    r.RescoredAt,
                    Score = r.AiSuggestedScore!.Value,
                    Level = r.AiSuggestedLevel
                })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var winners = rows
            .GroupBy(x => x.EntityId)
            .Select(g => g
                .OrderByDescending(x => x.CreatedAt)
                .ThenByDescending(x => x.GeneratedAt)
                .First())
            .ToList();

        if (winners.Count == 0)
            return new Dictionary<Guid, EntityLatestScoreProjection>();

        var winnerResultIds = winners.Select(w => w.ResultId).ToList();
        var breakdowns = await _dbContext.AnalysisResults
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && winnerResultIds.Contains(r.ResultId))
            .Select(r => new { r.ResultId, r.ScoreBreakdownJson })
            .ToDictionaryAsync(r => r.ResultId, r => r.ScoreBreakdownJson, ct)
            .ConfigureAwait(false);

        return winners.ToDictionary(
            latest => latest.EntityId,
            latest => new EntityLatestScoreProjection(
                latest.EntityId,
                latest.AnalysisId,
                latest.Score,
                latest.Level,
                latest.RescoredAt ?? latest.GeneratedAt)
            {
                ResultId           = latest.ResultId,
                EnrichmentStatus   = latest.EnrichmentStatus,
                AnalysisKind       = latest.AnalysisKind,
                FiscalYear         = latest.FiscalYear,
                GeneratedAt        = latest.GeneratedAt,
                RescoredAt         = latest.RescoredAt,
                ScoreBreakdownJson = breakdowns.GetValueOrDefault(latest.ResultId),
            });
    }

    /// <inheritdoc />
    public async Task<bool> TryConfirmFromPreApprovedAsync(
        Guid              analysisId,
        string            tenantId,
        Guid              reviewedBy,
        DateTime          reviewedAt,
        decimal?          decisionAmount,
        string?           decisionCurrency,
        string?           confirmedOperationsJson,
        string?           reviewNotes,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // Single atomic conditional UPDATE guarded on Status, mirroring
        // CreditLineRepository.SetStatusAsync. The Status predicate is the whole point: a concurrent
        // voter that already crossed the quorum has moved the row off 'PreApproved', so this matches
        // 0 rows and the caller reports the analysis as already confirmed instead of overwriting the
        // other manager's ReviewedBy with its own.
        //
        // The pre-approval columns are deliberately NOT written. On a confirmation they are the
        // first of the two signatures and the reason the second one matters.
        //
        // ReviewNotes reads its own current value when the caller supplied none, which is how the
        // endpoint's "only overwrite when supplied" rule survives a set-based update that otherwise
        // writes every column it names.
        var affected = await _dbContext.Analyses
            .Where(a => a.AnalysisId == analysisId
                     && a.TenantId   == tenantId
                     && a.Status     == PreApprovedStatus)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(a => a.Status,                  ApprovedStatus)
                .SetProperty(a => a.ReviewedBy,              (Guid?)reviewedBy)
                .SetProperty(a => a.ReviewedAt,              (DateTime?)reviewedAt)
                .SetProperty(a => a.DecisionAmount,          decisionAmount)
                .SetProperty(a => a.DecisionCurrency,        decisionCurrency)
                .SetProperty(a => a.ConfirmedOperationsJson, confirmedOperationsJson)
                .SetProperty(a => a.ReviewNotes,             a => reviewNotes ?? a.ReviewNotes),
                ct)
            .ConfigureAwait(false);

        if (affected == 0)
        {
            // Not an error: the expected outcome for whichever voter loses a simultaneous crossing.
            _logger.LogInformation(
                "Quorum crossing found analysis {AnalysisId} tenant {TenantId} no longer PreApproved; it was already settled by another decider.",
                analysisId, tenantId);

            return false;
        }

        _logger.LogInformation(
            "Analysis confirmed by quorum: {AnalysisId} tenant {TenantId} by {ReviewerId}",
            analysisId, tenantId, reviewedBy);

        return true;
    }

    /// <inheritdoc />
    public async Task<BalanceBaseFiguresProjection?> GetBalanceBaseFiguresAsync(
        Guid analysisId, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // Filtered before the projection so JsonValue sits in the top-level select, where the InMemory
        // provider can client-evaluate it (see SqlJsonFunctions). The stored keys are snake_case.
        return await _dbContext.AnalysisResults
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.AnalysisId == analysisId)
            .Select(r => new BalanceBaseFiguresProjection
            {
                HasBalance         = r.BalanceJson != null,
                Assets             = SqlJsonFunctions.JsonValue(r.BalanceJson, "$.assets"),
                CurrentAssets      = SqlJsonFunctions.JsonValue(r.BalanceJson, "$.current_assets"),
                CurrentLiabilities = SqlJsonFunctions.JsonValue(r.BalanceJson, "$.current_liabilities"),
                Liabilities        = SqlJsonFunctions.JsonValue(r.BalanceJson, "$.liabilities"),
                Equity             = SqlJsonFunctions.JsonValue(r.BalanceJson, "$.equity"),
                NetRevenue         = SqlJsonFunctions.JsonValue(r.BalanceJson, "$.net_revenue"),
                NetProfit          = SqlJsonFunctions.JsonValue(r.BalanceJson, "$.net_profit"),
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }
}
