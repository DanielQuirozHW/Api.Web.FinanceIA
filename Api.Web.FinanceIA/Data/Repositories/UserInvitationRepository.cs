// Project : Api.Web.FinanceIA
// Created : 2026-05-03
// Modified: 2026-05-03

using Api.Web.FinanceIA.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IUserInvitationRepository"/>.
/// Read queries use AsNoTracking. Write operations call
/// ChangeTracker.DetectChanges() manually because AutoDetectChangesEnabled
/// is disabled on <see cref="FinanciaDbContext"/>.
/// </summary>
public sealed class UserInvitationRepository : IUserInvitationRepository
{
    private readonly FinanciaDbContext _dbContext;
    private readonly ILogger<UserInvitationRepository> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="UserInvitationRepository"/>.
    /// </summary>
    public UserInvitationRepository(
        FinanciaDbContext dbContext,
        ILogger<UserInvitationRepository> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<UserInvitation?> GetByTokenAsync(
        string token,
        string tenantId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var now = DateTime.UtcNow;

        return await _dbContext.UserInvitations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.Token == token
                     && x.TenantId == tenantId
                     && x.UsedAt == null
                     && x.RevokedAt == null
                     && x.ExpiresAt > now,
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserInvitation>> GetByTenantAsync(
        string tenantId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return await _dbContext.UserInvitations
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CreateAsync(UserInvitation invitation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(invitation);

        _dbContext.UserInvitations.Add(invitation);
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "UserInvitation created: {InvitationId} for tenant {TenantId}",
            invitation.InvitationId,
            invitation.TenantId);
    }

    /// <inheritdoc />
    public async Task MarkAsUsedAsync(Guid invitationId, CancellationToken ct)
    {
        UserInvitation? invitation = await _dbContext.UserInvitations
            .FindAsync([invitationId], ct)
            .ConfigureAwait(false);

        if (invitation is null)
        {
            _logger.LogWarning(
                "UserInvitation not found for MarkAsUsed: {InvitationId}",
                invitationId);
            return;
        }

        invitation.UsedAt = DateTime.UtcNow;
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<UserInvitation?> GetByIdAsync(
        Guid invitationId,
        string tenantId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return await _dbContext.UserInvitations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.InvitationId == invitationId && x.TenantId == tenantId,
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RotateTokenAsync(
        Guid invitationId,
        string newToken,
        DateTime newExpiresAt,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newToken);

        UserInvitation? invitation = await _dbContext.UserInvitations
            .FindAsync([invitationId], ct)
            .ConfigureAwait(false);

        if (invitation is null)
        {
            _logger.LogWarning(
                "UserInvitation not found for RotateToken: {InvitationId}",
                invitationId);
            return;
        }

        invitation.Token = newToken;
        invitation.ExpiresAt = newExpiresAt;
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RevokeAsync(
        Guid invitationId,
        Guid? revokedByUserId,
        DateTime revokedAt,
        CancellationToken ct)
    {
        UserInvitation? invitation = await _dbContext.UserInvitations
            .FindAsync([invitationId], ct)
            .ConfigureAwait(false);

        if (invitation is null)
        {
            _logger.LogWarning(
                "UserInvitation not found for Revoke: {InvitationId}",
                invitationId);
            return;
        }

        invitation.RevokedAt = revokedAt;
        invitation.RevokedByUserId = revokedByUserId;
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
