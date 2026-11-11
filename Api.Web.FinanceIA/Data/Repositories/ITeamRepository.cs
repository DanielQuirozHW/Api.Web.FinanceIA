// Project : Api.Web.FinanceIA
// Created : 2026-09-10

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Persistence operations for <see cref="Team"/> records
/// in the per-tenant business database.
/// </summary>
/// <remarks>
/// Every method is scoped by tenant. <see cref="FinanciaDbContext"/> has no global query filter, so
/// the predicate is written by hand on every query — including the lookups keyed by
/// <see cref="Team.TeamId"/>, which is globally unique and would otherwise resolve a team belonging
/// to another tenant.
/// </remarks>
public interface ITeamRepository
{
    /// <summary>
    /// Returns every team of one tenant, ordered by Name.
    /// </summary>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<IReadOnlyList<Team>> GetByTenantAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns one team, or null when no team matches BOTH the id and the tenant.
    /// </summary>
    /// <param name="teamId">The team to look up.</param>
    /// <param name="tenantId">
    /// Tenant scope. Part of the predicate, not just a stamp: TeamId is globally unique, so without
    /// it a caller could read another tenant's team by guessing a GUID.
    /// </param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<Team?> GetByIdAsync(Guid teamId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Persists a new team.
    /// </summary>
    /// <param name="team">The team to create. The application supplies the TeamId.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task CreateAsync(Team team, CancellationToken ct);

    /// <summary>
    /// Persists changes to an existing team.
    /// </summary>
    /// <param name="team">The tracked team to update.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task UpdateAsync(Team team, CancellationToken ct);

    /// <summary>
    /// Deletes one team, tenant-scoped.
    /// </summary>
    /// <param name="teamId">The team to delete.</param>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <returns>False when no team matches both the id and the tenant.</returns>
    /// <remarks>
    /// A HARD delete — there is no soft-delete precedent for a maintenance table here, and a
    /// tombstoned team would still have to be filtered out of every list. The caller MUST unassign
    /// the team's members first (see <c>IUserRepository.ClearTeamAsync</c>), otherwise
    /// FK_Users_Team rejects the delete.
    /// </remarks>
    Task<bool> DeleteAsync(Guid teamId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns the UserIds of EVERY manager of one team, tenant-scoped. Empty when the team has no
    /// managers, or does not belong to this tenant.
    /// </summary>
    /// <param name="teamId">The team whose managers to read.</param>
    /// <param name="tenantId">Tenant scope (always enforced, as on every method here).</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <remarks>
    /// The read behind the multi-manager notification path (DDL 064). Served as a covering seek by
    /// PK_TeamManagers, which is clustered on (TeamId, UserId) — which is why that table carries no
    /// secondary index.
    ///
    /// <para>
    /// An empty result is NOT the same as "this team has no manager". <see cref="Team.ManagerUserId"/>
    /// is retained but deprecated, and a tenant whose DDL 066 data migration has not run still has
    /// its manager only in that column. <c>AnalysisDecisionNotifier</c> therefore reads the UNION of
    /// the two and de-duplicates; callers that need "the managers of this team" must do the same
    /// until the deprecated column is dropped.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<Guid>> GetManagerUserIdsAsync(Guid teamId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns the manager UserIds of EVERY team of one tenant, keyed by TeamId. Teams with no
    /// managers are simply absent from the dictionary rather than present with an empty list.
    /// </summary>
    /// <param name="tenantId">Tenant scope (always enforced, as on every method here).</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <remarks>
    /// The batch form of <see cref="GetManagerUserIdsAsync"/>, and it exists for exactly one reason:
    /// the teams LIST endpoint needs every team's managers, and calling the single-team read in a loop
    /// would make that endpoint's query count grow with the number of teams. One query covers the
    /// tenant instead, which is what keeps <c>GetTeamsAsync</c> at a fixed three.
    ///
    /// <para>
    /// The same caveat as on <see cref="GetManagerUserIdsAsync"/> applies: an absent team is NOT
    /// necessarily a team without a manager, because the deprecated <see cref="Team.ManagerUserId"/>
    /// may still hold one on a tenant whose DDL 066 never ran. Callers must union the two.
    /// </para>
    /// </remarks>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetManagerUserIdsByTenantAsync(
        string tenantId, CancellationToken ct);

    /// <summary>
    /// Replaces the complete set of managers for one team, tenant-scoped. Whatever was there before
    /// is gone; <paramref name="userIds"/> is the new truth.
    /// </summary>
    /// <param name="teamId">The team whose managers to replace.</param>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="userIds">
    /// The new manager set. Duplicates are collapsed. Pass an EMPTY collection to clear the team's
    /// managers entirely — that is the supported way to do it, and what the delete path below needs.
    /// </param>
    /// <param name="createdBy">
    /// UserId of whoever is making the change, or null when there is no application user behind it.
    /// </param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <remarks>
    /// Replace-the-set rather than add/remove, following <c>IUserApprovalLimitRepository.ReplaceForUserAsync</c>:
    /// the caller always knows the whole intended set, and a single atomic replace cannot leave the
    /// team half-updated the way a sequence of adds and removes can.
    ///
    /// <para>
    /// ⚠ The caller MUST clear a team's managers — call this with an empty collection — BEFORE
    /// <see cref="DeleteAsync"/>, otherwise FK_TeamManagers_Team rejects the delete. This is the same
    /// contract FK_Users_Team already imposes via <c>IUserRepository.ClearTeamAsync</c>; a team
    /// delete has to satisfy both.
    /// </para>
    ///
    /// <para>
    /// This does NOT write <see cref="Team.ManagerUserId"/>. That column is retained but deprecated
    /// and is maintained separately by <c>TeamsController</c>, which keeps the two in step so the
    /// existing single-manager Admin UI stays correct.
    /// </para>
    /// </remarks>
    Task ReplaceManagersAsync(
        Guid                     teamId,
        string                   tenantId,
        IReadOnlyCollection<Guid> userIds,
        Guid?                    createdBy,
        CancellationToken        ct);
}
