// Project : Api.Web.FinanceIA
// Created : 2026-09-10

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ITeamRepository"/>.
/// Read queries use AsNoTracking. Write operations call
/// ChangeTracker.DetectChanges() manually because AutoDetectChangesEnabled
/// is disabled on <see cref="FinanciaDbContext"/>.
/// </summary>
public sealed class TeamRepository : ITeamRepository
{
    private readonly FinanciaDbContext       _dbContext;
    private readonly ILogger<TeamRepository> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="TeamRepository"/>.
    /// </summary>
    public TeamRepository(
        FinanciaDbContext dbContext,
        ILogger<TeamRepository> logger)
    {
        _dbContext = dbContext;
        _logger    = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Team>> GetByTenantAsync(string tenantId, CancellationToken ct)
    {
        // (TenantId, Name) is exactly IX_Teams_TenantId_Name, so the ordering comes from the index
        // rather than a sort.
        return await _dbContext.Teams
            .AsNoTracking()
            .Where(t => t.TenantId == tenantId)
            .OrderBy(t => t.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Team?> GetByIdAsync(Guid teamId, string tenantId, CancellationToken ct)
    {
        return await _dbContext.Teams
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TeamId == teamId && t.TenantId == tenantId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CreateAsync(Team team, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(team);

        _dbContext.Teams.Add(team);
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Created team {TeamId} for tenant {TenantId}", team.TeamId, team.TenantId);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(Team team, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(team);

        _dbContext.Teams.Update(team);
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Updated team {TeamId} for tenant {TenantId}", team.TeamId, team.TenantId);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid teamId, string tenantId, CancellationToken ct)
    {
        // Tracked load (no AsNoTracking) so Remove has an entity to work from, and tenant-scoped so
        // a team of another tenant is simply "not found" rather than deleted.
        var team = await _dbContext.Teams
            .FirstOrDefaultAsync(t => t.TeamId == teamId && t.TenantId == tenantId, ct)
            .ConfigureAwait(false);

        if (team is null) return false;

        _dbContext.Teams.Remove(team);
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Deleted team {TeamId} for tenant {TenantId}", teamId, tenantId);

        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> GetManagerUserIdsAsync(
        Guid teamId, string tenantId, CancellationToken ct)
    {
        // Projects to the id, never to the entity — there is nothing else on the row a caller wants.
        // (TeamId, TenantId) seeks PK_TeamManagers, which is clustered on (TeamId, UserId), so this
        // is a covering seek with no key lookup.
        return await _dbContext.TeamManagers
            .AsNoTracking()
            .Where(m => m.TeamId == teamId && m.TenantId == tenantId)
            .Select(m => m.UserId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetManagerUserIdsByTenantAsync(
        string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // Projects to the two columns the caller wants, never to the entity. Grouped in memory rather
        // than with a GroupBy in the query: the set is one tenant's manager rows — a handful per team
        // — and a server-side grouping would buy nothing while forcing the provider into a shape the
        // in-memory provider used by the tests handles differently from SQL Server.
        var rows = await _dbContext.TeamManagers
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId)
            .Select(m => new { m.TeamId, m.UserId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => r.TeamId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<Guid>)g.Select(r => r.UserId).ToList());
    }

    /// <inheritdoc />
    public async Task ReplaceManagersAsync(
        Guid                      teamId,
        string                    tenantId,
        IReadOnlyCollection<Guid> userIds,
        Guid?                     createdBy,
        CancellationToken         ct)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // Tracked load (no AsNoTracking) so RemoveRange has entities to work from, and tenant-scoped
        // so a team of another tenant has no managers to replace rather than losing them.
        var existing = await _dbContext.TeamManagers
            .Where(m => m.TeamId == teamId && m.TenantId == tenantId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Distinct because PK_TeamManagers is (TeamId, UserId): the same id twice in one call would
        // otherwise be two Added entities for one key, which SaveChanges rejects outright.
        var wanted = userIds.Distinct().ToList();

        // RECONCILE the set rather than deleting it and re-inserting it wholesale. Two reasons, and
        // the first is the one that bites:
        //
        //   1. A row that survives keeps its ORIGINAL CreatedAt/CreatedBy. TeamsController mirrors
        //      the manager on EVERY PUT, so re-saving a team without touching its manager is the
        //      common path — and a blind replace would re-stamp the audit columns every time an
        //      Admin merely renamed the team, destroying the record of when the manager was
        //      actually named.
        //   2. It never puts a Deleted and an Added entity with the same composite key into one
        //      SaveChanges, so there is no identity-resolution subtlety to rely on.
        //
        // Both lists are tiny — a team has a handful of managers — so the O(n*m) membership tests
        // are cheaper than any lookup structure would be to build.
        var toRemove = existing.Where(m => !wanted.Contains(m.UserId)).ToList();
        var toAdd    = wanted.Where(u => !existing.Any(m => m.UserId == u)).ToList();

        if (toRemove.Count == 0 && toAdd.Count == 0)
        {
            // Nothing to do. Returning here skips a SaveChanges that would issue no statements, and
            // is the usual outcome of a plain team rename.
            return;
        }

        if (toRemove.Count > 0) _dbContext.TeamManagers.RemoveRange(toRemove);

        var now = DateTime.UtcNow;

        foreach (var userId in toAdd)
        {
            _dbContext.TeamManagers.Add(new TeamManager
            {
                TeamId    = teamId,
                UserId    = userId,
                // The TEAM's tenant, not the user's — the row records "this team has this manager".
                TenantId  = tenantId,
                CreatedAt = now,
                CreatedBy = createdBy
            });
        }

        // AutoDetectChangesEnabled is off on this context, as every other write here notes.
        _dbContext.ChangeTracker.DetectChanges();

        // One SaveChanges for the removals and the additions together, so the set is never
        // half-replaced.
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        // Counts and ids only — no names, no emails.
        _logger.LogInformation(
            "Replaced managers of team {TeamId} for tenant {TenantId}: removed={RemovedCount} added={AddedCount} unchanged={UnchangedCount}",
            teamId, tenantId, toRemove.Count, toAdd.Count, existing.Count - toRemove.Count);
    }
}
