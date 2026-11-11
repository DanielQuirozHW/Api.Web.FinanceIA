// Project : Api.Web.FinanceIA
// Created : 2026-09-04

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IUserApprovalLimitRepository"/>.
/// Reads use AsNoTracking; the write path loads TRACKED rows (it has to diff against them) and
/// calls DetectChanges before SaveChangesAsync, because the context disables AutoDetectChanges.
/// </summary>
public sealed class UserApprovalLimitRepository : IUserApprovalLimitRepository
{
    private readonly FinanciaDbContext _dbContext;
    private readonly ILogger<UserApprovalLimitRepository> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="UserApprovalLimitRepository"/>.
    /// </summary>
    public UserApprovalLimitRepository(
        FinanciaDbContext dbContext,
        ILogger<UserApprovalLimitRepository> logger)
    {
        _dbContext = dbContext;
        _logger    = logger;
    }

    /// <inheritdoc />
    public async Task<UserApprovalLimit?> GetAsync(
        Guid userId, string currency, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return await _dbContext.UserApprovalLimits
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.UserId == userId
                  && x.Currency == currency
                  && x.TenantId == tenantId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserApprovalLimit>> GetByUserAsync(
        Guid userId, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return await _dbContext.UserApprovalLimits
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.TenantId == tenantId)
            .OrderBy(x => x.Currency)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserApprovalLimit>> ReplaceForUserAsync(
        Guid userId,
        string tenantId,
        IReadOnlyCollection<UserApprovalLimit> limits,
        Guid editedBy,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(limits);

        // TRACKED, unlike every read above: these rows are about to be updated or deleted in place,
        // and the diff below needs EF to see the changes it makes to them.
        var existing = await _dbContext.UserApprovalLimits
            .Where(x => x.UserId == userId && x.TenantId == tenantId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var now      = DateTime.UtcNow;
        var incoming = limits.ToDictionary(x => x.Currency, StringComparer.Ordinal);

        // Currencies the caller no longer lists are removed outright. Deleting is deliberately not
        // the same as storing CanApprove = false: an absent row is the "never configured" default,
        // which is what an Admin clearing a currency means.
        foreach (var row in existing.Where(row => !incoming.ContainsKey(row.Currency)))
        {
            _dbContext.UserApprovalLimits.Remove(row);
        }

        var byCurrency = existing.ToDictionary(x => x.Currency, StringComparer.Ordinal);

        foreach (var desired in limits)
        {
            if (byCurrency.TryGetValue(desired.Currency, out var row))
            {
                row.CanApprove = desired.CanApprove;
                row.MaxAmount  = desired.MaxAmount;
                row.UpdatedAt  = now;
                row.UpdatedBy  = editedBy;
            }
            else
            {
                _dbContext.UserApprovalLimits.Add(new UserApprovalLimit
                {
                    TenantId   = tenantId,
                    UserId     = userId,
                    Currency   = desired.Currency,
                    CanApprove = desired.CanApprove,
                    MaxAmount  = desired.MaxAmount,
                    CreatedAt  = now,
                    CreatedBy  = editedBy,
                    UpdatedAt  = now,
                    UpdatedBy  = editedBy,
                });
            }
        }

        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        // The amounts are deliberately absent from this line: which analyst may commit how much is
        // exactly the kind of detail that does not belong in an application log.
        _logger.LogInformation(
            "Approval limits replaced for user {UserId} tenant {TenantId}: {CurrencyCount} currencies, by {EditedBy}",
            userId, tenantId, limits.Count, editedBy);

        return await GetByUserAsync(userId, tenantId, ct).ConfigureAwait(false);
    }
}
