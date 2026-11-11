// Project : Api.Web.FinanceIA
// Created : 2026-05-02
// Modified: 2026-05-02

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IUserRepository"/>.
/// Read queries use AsNoTracking. Write operations call
/// ChangeTracker.DetectChanges() manually because AutoDetectChangesEnabled
/// is disabled on <see cref="FinanciaDbContext"/>.
/// </summary>
public sealed class UserRepository : IUserRepository
{
    private readonly FinanciaDbContext _dbContext;
    private readonly ILogger<UserRepository> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="UserRepository"/>.
    /// </summary>
    public UserRepository(FinanciaDbContext dbContext, ILogger<UserRepository> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firebaseUid);

        return await _dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.FirebaseUid == firebaseUid, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct)
    {
        return await _dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == userId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<User?> GetByEmailAsync(string email, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var normalised = email.ToLowerInvariant();

        return await _dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Email == normalised, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firebaseUid);

        return await _dbContext.Users
            .AsNoTracking()
            .AnyAsync(x => x.FirebaseUid == firebaseUid, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct)
    {
        var ids = userIds.ToList();
        if (ids.Count == 0) return [];

        return await _dbContext.Users
            .AsNoTracking()
            .Where(u => ids.Contains(u.UserId))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return await _dbContext.Users
            .AsNoTracking()
            .Where(u => u.TenantId == tenantId && u.IsActive)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<User> CreateAsync(User user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);

        _dbContext.Users.Add(user);
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "User created: {UserId} for tenant {TenantId}",
            user.UserId,
            user.TenantId);

        return user;
    }

    /// <inheritdoc />
    public async Task UpdateLastLoginAsync(Guid userId, CancellationToken ct)
    {
        await _dbContext.Users
            .Where(x => x.UserId == userId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.LastLoginAt, DateTime.UtcNow),
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateOnboardingFlagsAsync(Guid userId, string? flagsJson, CancellationToken ct)
    {
        await _dbContext.Users
            .Where(x => x.UserId == userId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.OnboardingFlagsJson, flagsJson),
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct)
    {
        await _dbContext.Users
            .Where(x => x.UserId == userId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.TeamId, teamId),
                ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "User {UserId} team assignment set to {TeamId}", userId, teamId);
    }

    /// <inheritdoc />
    public async Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct)
    {
        // Tenant-scoped as well as team-scoped: TeamId is globally unique, but the extra predicate
        // keeps this consistent with every other write that could reach across a tenant boundary.
        var cleared = await _dbContext.Users
            .Where(x => x.TeamId == teamId && x.TenantId == tenantId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.TeamId, (Guid?)null),
                ct)
            .ConfigureAwait(false);

        if (cleared > 0)
        {
            _logger.LogInformation(
                "Unassigned {Count} user(s) from team {TeamId} tenant {TenantId}",
                cleared, teamId, tenantId);
        }

        return cleared;
    }
}
