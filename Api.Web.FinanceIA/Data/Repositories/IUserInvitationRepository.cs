// Project : Api.Web.FinanceIA
// Created : 2026-05-03
// Modified: 2026-05-03

using Api.Web.FinanceIA.Models.Entities;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Persistence operations for <see cref="UserInvitation"/> entities
/// in the per-tenant business database.
/// </summary>
public interface IUserInvitationRepository
{
    /// <summary>
    /// Returns the active, unexpired invitation matching the given token and tenant,
    /// or null if not found, already used, or expired.
    /// </summary>
    /// <param name="token">The unique invitation token.</param>
    /// <param name="tenantId">The tenant that owns the invitation.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<UserInvitation?> GetByTokenAsync(string token, string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns every invitation for the given tenant regardless of state
    /// (pending, accepted, expired or revoked), ordered by creation time desc.
    /// Used by the admin listing, which derives status per row.
    /// </summary>
    /// <param name="tenantId">The tenant that owns the invitations.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<IReadOnlyList<UserInvitation>> GetByTenantAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Persists a new invitation record.
    /// </summary>
    /// <param name="invitation">The invitation entity to create.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task CreateAsync(UserInvitation invitation, CancellationToken ct);

    /// <summary>
    /// Sets <c>UsedAt</c> to the current UTC time for the specified invitation,
    /// marking it as consumed.
    /// </summary>
    /// <param name="invitationId">The identifier of the invitation to mark as used.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task MarkAsUsedAsync(Guid invitationId, CancellationToken ct);

    /// <summary>
    /// Returns the invitation with the given id scoped to the tenant, regardless of
    /// state (used, expired or revoked), or null. Used by the resend/revoke flows,
    /// which must inspect terminal states — unlike <see cref="GetByTokenAsync"/>.
    /// </summary>
    /// <param name="invitationId">The invitation identifier.</param>
    /// <param name="tenantId">The tenant that owns the invitation.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<UserInvitation?> GetByIdAsync(Guid invitationId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Rotates the token and expiry of an existing invitation (resend), keeping the
    /// same <c>InvitationId</c>. No-op if the invitation does not exist.
    /// </summary>
    /// <param name="invitationId">The invitation to update.</param>
    /// <param name="newToken">The freshly generated token.</param>
    /// <param name="newExpiresAt">The new UTC expiry timestamp.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task RotateTokenAsync(Guid invitationId, string newToken, DateTime newExpiresAt, CancellationToken ct);

    /// <summary>
    /// Marks an invitation as revoked by setting <c>RevokedAt</c> and
    /// <c>RevokedByUserId</c>. No-op if the invitation does not exist.
    /// </summary>
    /// <param name="invitationId">The invitation to revoke.</param>
    /// <param name="revokedByUserId">
    /// The admin performing the revocation, or null when the first-admin bootstrap replaces its own
    /// earlier invitation (there is no admin yet).
    /// </param>
    /// <param name="revokedAt">The UTC revocation timestamp.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task RevokeAsync(Guid invitationId, Guid? revokedByUserId, DateTime revokedAt, CancellationToken ct);
}
