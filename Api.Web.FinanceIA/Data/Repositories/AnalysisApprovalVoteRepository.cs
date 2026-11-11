// Project : Api.Web.FinanceIA
// Created : 2026-09-16

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IAnalysisApprovalVoteRepository"/>.
/// Read queries use AsNoTracking. Write operations call ChangeTracker.DetectChanges() manually
/// because AutoDetectChangesEnabled is disabled on <see cref="FinanciaDbContext"/>.
/// </summary>
public sealed class AnalysisApprovalVoteRepository : IAnalysisApprovalVoteRepository
{
    /// <summary>
    /// The unique constraint DDL 067 declares on (AnalysisId, UserId). Matched against the provider
    /// error text to tell "this manager already voted" apart from any other write failure, which must
    /// still surface. The name is duplicated in AnalysisApprovalVoteConfiguration on purpose — the
    /// two must stay in step, and a mismatch degrades this into the slower pre-read path rather than
    /// breaking anything.
    /// </summary>
    private const string UniqueVoteConstraint = "UQ_AnalysisApprovalVotes_Analysis_User";

    private readonly FinanciaDbContext                        _dbContext;
    private readonly ILogger<AnalysisApprovalVoteRepository>  _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="AnalysisApprovalVoteRepository"/>.
    /// </summary>
    public AnalysisApprovalVoteRepository(
        FinanciaDbContext dbContext,
        ILogger<AnalysisApprovalVoteRepository> logger)
    {
        _dbContext = dbContext;
        _logger    = logger;
    }

    /// <inheritdoc />
    public async Task<bool> RecordAsync(AnalysisApprovalVote vote, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vote);
        ArgumentException.ThrowIfNullOrWhiteSpace(vote.TenantId);

        // Fast path. A manager pressing the button twice is the ordinary case, and answering it from
        // the index costs one seek. It does NOT make the insert below safe on its own — two requests
        // can both read "no vote" before either writes — which is exactly why the catch exists.
        var alreadyVoted = await _dbContext.AnalysisApprovalVotes
            .AsNoTracking()
            .AnyAsync(v => v.AnalysisId == vote.AnalysisId
                        && v.UserId     == vote.UserId
                        && v.TenantId   == vote.TenantId, ct)
            .ConfigureAwait(false);

        if (alreadyVoted) return false;

        _dbContext.AnalysisApprovalVotes.Add(vote);
        _dbContext.ChangeTracker.DetectChanges();

        try
        {
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsDuplicateVote(ex))
        {
            // The race the pre-read cannot close: another request inserted this manager's vote
            // between the read above and this write. The constraint decided it, which is what DDL 067
            // declared it for. Detach so the failed insert does not poison a later SaveChanges on
            // this request-scoped context.
            _dbContext.Entry(vote).State = EntityState.Detached;

            _logger.LogInformation(
                "Duplicate approval vote rejected by the unique constraint: analysis {AnalysisId} user {UserId} tenant {TenantId}",
                vote.AnalysisId, vote.UserId, vote.TenantId);

            return false;
        }

        _logger.LogInformation(
            "Approval vote recorded: analysis {AnalysisId} user {UserId} tenant {TenantId} vote={Vote}",
            vote.AnalysisId, vote.UserId, vote.TenantId, vote.Vote);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> WithdrawAsync(Guid analysisId, Guid userId, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // Tracked load (no AsNoTracking) so Remove has an entity to work from. All three predicates
        // are load-bearing: userId is what makes it impossible to withdraw someone else's signature,
        // and tenantId is what keeps a globally unique AnalysisId from reaching another tenant.
        var vote = await _dbContext.AnalysisApprovalVotes
            .FirstOrDefaultAsync(v => v.AnalysisId == analysisId
                                   && v.UserId     == userId
                                   && v.TenantId   == tenantId, ct)
            .ConfigureAwait(false);

        if (vote is null) return false;

        _dbContext.AnalysisApprovalVotes.Remove(vote);
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Approval vote withdrawn: analysis {AnalysisId} user {UserId} tenant {TenantId}",
            analysisId, userId, tenantId);

        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> GetApproverUserIdsAsync(
        Guid analysisId, string tenantId, CancellationToken ct)
    {
        return await _dbContext.AnalysisApprovalVotes
            .AsNoTracking()
            .Where(v => v.AnalysisId == analysisId
                     && v.TenantId   == tenantId
                     && v.Vote       == AnalysisVoteValues.Approved)
            .Select(v => v.UserId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetApproverUserIdsByAnalysesAsync(
        IReadOnlyCollection<Guid> analysisIds, string tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(analysisIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (analysisIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<Guid>>();

        var ids = analysisIds.Distinct().ToList();

        // The pair projection, distinct, then grouped in memory — the same reason
        // TeamRepository.GetManagerUserIdsByTenantAsync gives: a handful of rows per analysis, and a
        // server-side GroupBy would only force a shape the in-memory test provider treats differently.
        var rows = await _dbContext.AnalysisApprovalVotes
            .AsNoTracking()
            .Where(v => ids.Contains(v.AnalysisId)
                     && v.TenantId == tenantId
                     && v.Vote     == AnalysisVoteValues.Approved)
            .Select(v => new { v.AnalysisId, v.UserId })
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => r.AnalysisId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<Guid>)g.Select(r => r.UserId).ToList());
    }

    /// <inheritdoc />
    public async Task<int> DeleteByAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // Tracked load, for the same reason WithdrawAsync uses one: RemoveRange needs entities.
        // Deliberately NOT ExecuteDeleteAsync — the vote count is bounded by a team's manager roster
        // (single digits), so there is nothing to win, and a tracked delete keeps this method
        // exercisable by the in-memory provider the Data/ tests use.
        var votes = await _dbContext.AnalysisApprovalVotes
            .Where(v => v.AnalysisId == analysisId && v.TenantId == tenantId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (votes.Count == 0) return 0;

        _dbContext.AnalysisApprovalVotes.RemoveRange(votes);
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Approval votes discarded: analysis {AnalysisId} tenant {TenantId} count={VoteCount}",
            analysisId, tenantId, votes.Count);

        return votes.Count;
    }

    /// <summary>
    /// Whether a write failure is the unique constraint refusing a second vote from one manager.
    /// </summary>
    /// <remarks>
    /// Matched on the constraint NAME in the provider's message rather than on a SQL error number,
    /// so it cannot mistake some other unique index on this table for this one. Any failure that is
    /// not this is rethrown — a vote silently swallowed is the failure mode that matters here.
    /// </remarks>
    private static bool IsDuplicateVote(DbUpdateException ex)
        => ex.InnerException?.Message.Contains(UniqueVoteConstraint, StringComparison.OrdinalIgnoreCase) == true;
}
