// Project : Api.Web.FinanceIA
// Created : 2026-05-02
// Modified: 2026-05-02

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Persistence operations for <see cref="User"/> entities
/// in the per-tenant business database.
/// </summary>
public interface IUserRepository
{
    /// <summary>
    /// Returns the user with the specified Firebase UID, or null if not found.
    /// </summary>
    /// <param name="firebaseUid">The Firebase UID to look up.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct);

    /// <summary>
    /// Returns the user with the specified UserId, or null if not found.
    /// </summary>
    /// <param name="userId">The internal user identifier (GUID).</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// Returns the user with the specified email address, or null if not found.
    /// Email comparison is case-insensitive (stored and compared in lowercase).
    /// </summary>
    /// <param name="email">The email address to look up.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<User?> GetByEmailAsync(string email, CancellationToken ct);

    /// <summary>
    /// Returns true if a user with the specified Firebase UID already exists.
    /// </summary>
    /// <param name="firebaseUid">The Firebase UID to check.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<bool> ExistsByFirebaseUidAsync(string firebaseUid, CancellationToken ct);

    /// <summary>
    /// Returns users matching any of the specified user IDs.
    /// </summary>
    /// <param name="userIds">The user identifiers to look up.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct);

    /// <summary>
    /// Returns every ACTIVE user of one tenant.
    /// </summary>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <remarks>
    /// The fan-out primitive behind per-user notifications: a tenant-wide event has to become one
    /// notification per user, so the emitter needs the roster.
    ///
    /// <para>
    /// Deactivated accounts are excluded deliberately. A notification is a call to action for a
    /// person who can still log in; giving a disabled account an unread badge it can never clear
    /// is just rows. Note this is one of the few methods on this interface that scopes by tenant —
    /// the lookups above are keyed by globally unique values (Firebase UID, UserId, email) and
    /// predate the row-level tenancy convention.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Persists a new user record and returns the saved entity.
    /// </summary>
    /// <param name="user">The user entity to create.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<User> CreateAsync(User user, CancellationToken ct);

    /// <summary>
    /// Updates only the <c>LastLoginAt</c> column for the specified user
    /// using an EF Core bulk update (no entity load).
    /// </summary>
    /// <param name="userId">The user whose last-login timestamp to update.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task UpdateLastLoginAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// Updates only the <c>OnboardingFlagsJson</c> column for the specified user
    /// using an EF Core bulk update (no entity load).
    /// </summary>
    /// <param name="userId">The user whose onboarding flags to update.</param>
    /// <param name="flagsJson">The serialised JSON blob to store, or null to clear.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task UpdateOnboardingFlagsAsync(Guid userId, string? flagsJson, CancellationToken ct);

    /// <summary>
    /// Updates only the <c>TeamId</c> column for the specified user
    /// using an EF Core bulk update (no entity load).
    /// </summary>
    /// <param name="userId">The user to assign.</param>
    /// <param name="teamId">The team to assign them to, or null to unassign.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <remarks>
    /// Not tenant-scoped, like the other single-user writes on this interface: UserId is globally
    /// unique. The caller is responsible for having established that the user AND the team belong to
    /// the caller's tenant — <c>SettingsController</c> does so via <c>EnsureUserInTenantAsync</c> and
    /// a tenant-scoped <c>ITeamRepository.GetByIdAsync</c> before calling this.
    /// </remarks>
    Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct);

    /// <summary>
    /// Unassigns every member of one team, setting their <c>TeamId</c> back to null.
    /// </summary>
    /// <param name="teamId">The team whose members to unassign.</param>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <returns>The number of users that were unassigned.</returns>
    /// <remarks>
    /// Called immediately before deleting a team. FK_Users_Team has NO CASCADE deliberately — a
    /// cascade would delete the analysts along with their team — so the members have to be let go
    /// first or the delete is rejected.
    /// </remarks>
    Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct);
}
