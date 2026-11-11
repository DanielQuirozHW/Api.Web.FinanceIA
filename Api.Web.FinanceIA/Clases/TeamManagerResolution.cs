// Project : Api.Web.FinanceIA
// Created : 2026-09-16

using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// THE single definition of "an active manager of a team". Shared by
/// <see cref="AnalysisDecisionNotifier"/>, which decides who is TOLD about a pre-approval, and by
/// <see cref="AnalysisApprovalQuorumService"/>, which decides how many signatures one needs.
/// </summary>
/// <remarks>
/// It is shared because the alternative is two definitions that drift. If the quorum counted a
/// manager the notifier never told, an analysis would wait for a signature from someone who does not
/// know it is pending; if the notifier told a manager the quorum did not count, their approval would
/// appear to do nothing. Both are silent failures of a credit decision.
///
/// <para>
/// A STATIC HELPER rather than an injected service, deliberately. Both callers already hold the two
/// repositories this needs, so making it a dependency would change
/// <see cref="AnalysisDecisionNotifier"/>'s constructor and every test built against it, buying
/// nothing: there is no state here and nothing to substitute — the seam that matters is the
/// repositories, which are already interfaces.
/// </para>
///
/// <para>
/// ACTIVENESS AND ELIGIBILITY ARE DIFFERENT QUESTIONS, and only the first is answered here. This
/// returns every active named manager whatever their role, because that is who the notifier must
/// reach. The quorum engine additionally filters to the roles that can actually confirm — see
/// <see cref="AnalysisApprovalQuorumService"/> for why it must.
/// </para>
/// </remarks>
internal static class TeamManagerResolution
{
    /// <summary>
    /// Resolves the ACTIVE named managers of one team, together with the tenant roster they were
    /// verified against so the caller can reuse it rather than query twice.
    /// </summary>
    /// <param name="teamRepository">Source of the team's manager set.</param>
    /// <param name="userRepository">Source of the tenant's active roster.</param>
    /// <param name="teamId">The team, or null when the user belongs to none — then no managers.</param>
    /// <param name="tenantId">Tenant slug scoping both reads.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <returns>
    /// The team row (null when there was no team to read), the active managers (empty when there is
    /// no team, none is named, or all are deactivated) and the tenant's active roster.
    /// </returns>
    public static async Task<TeamManagerSet> ResolveActiveManagersAsync(
            ITeamRepository   teamRepository,
            IUserRepository   userRepository,
            Guid?             teamId,
            string            tenantId,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(teamRepository);
        ArgumentNullException.ThrowIfNull(userRepository);

        var managerUserIds = new List<Guid>();
        Team? teamRow = null;

        if (teamId is Guid id)
        {
            // [dbo].[TeamManagers] (DDL 064) is the source of truth: a team may name several managers
            // and all of them count, because one manager is a single point of failure — they go on
            // holiday and the pre-approval simply waits.
            managerUserIds.AddRange(
                await teamRepository
                    .GetManagerUserIdsAsync(id, tenantId, ct)
                    .ConfigureAwait(false));

            // Teams.ManagerUserId is RETAINED BUT DEPRECATED, and it is UNIONED IN rather than
            // replaced. DDL 066 migrates it into the table above, but 066 is a DATA script and the
            // --verify-schema deploy gate compares COLUMNS, not rows — so a tenant whose 066 was
            // skipped would pass every check and still silently lose its named manager. Reading both
            // closes that window; Distinct() below collapses the duplicate 066 deliberately creates.
            teamRow = await teamRepository
                .GetByIdAsync(id, tenantId, ct)
                .ConfigureAwait(false);

            if (teamRow?.ManagerUserId is Guid legacyManagerId) managerUserIds.Add(legacyManagerId);
        }

        // The roster is needed either way: to verify the named managers are still active and in this
        // tenant, and by the notifier to build its fallback list. GetActiveByTenantAsync already
        // filters on Users.IsActive and Users.TenantId — those two ARE the definition of "active",
        // and there is no other flag anywhere on this path ([dbo].[TeamManagers] carries none).
        var roster = await userRepository
            .GetActiveByTenantAsync(tenantId, ct)
            .ConfigureAwait(false);

        return new TeamManagerSet(teamRow, IntersectActiveManagers(managerUserIds, IndexRoster(roster)), roster);
    }

    /// <summary>
    /// Indexes an active roster by user id, tolerating a user listed twice.
    /// </summary>
    /// <remarks>
    /// TryAdd rather than ToDictionary: a roster carrying the same user twice is odd data but
    /// nothing forbids it, and ToDictionary would THROW on the duplicate key — which the notifier
    /// would then swallow into "no recipients", silently losing a hand-off. The predecessor of
    /// this code used a linear Any() and tolerated it; this keeps the tolerance and the O(1).
    /// </remarks>
    public static IReadOnlyDictionary<Guid, User> IndexRoster(IReadOnlyList<User> roster)
    {
        ArgumentNullException.ThrowIfNull(roster);

        var rosterById = new Dictionary<Guid, User>();
        foreach (var user in roster) rosterById.TryAdd(user.UserId, user);
        return rosterById;
    }

    /// <summary>
    /// The named managers who are still on the active roster, de-duplicated, in the order named.
    /// </summary>
    /// <remarks>
    /// The pure half of <see cref="ResolveActiveManagersAsync"/>, split out so the quorum engine's
    /// BATCH read (<see cref="AnalysisApprovalQuorumService"/>) applies the very same rule to rows it
    /// read once for many analyses, rather than a second copy of it that could drift.
    /// </remarks>
    /// <param name="managerUserIds">
    /// The [dbo].[TeamManagers] ids plus the deprecated Teams.ManagerUserId when set; duplicates allowed.
    /// </param>
    /// <param name="rosterById">The tenant's active roster, from <see cref="IndexRoster"/>.</param>
    public static IReadOnlyList<User> IntersectActiveManagers(
        IEnumerable<Guid> managerUserIds, IReadOnlyDictionary<Guid, User> rosterById)
    {
        ArgumentNullException.ThrowIfNull(managerUserIds);
        ArgumentNullException.ThrowIfNull(rosterById);

        return managerUserIds
            .Distinct()
            .Where(rosterById.ContainsKey)
            .Select(managerId => rosterById[managerId])
            .ToList();
    }
}

/// <summary>
/// What <see cref="TeamManagerResolution.ResolveActiveManagersAsync"/> found.
/// </summary>
/// <param name="Team">
/// The team row, or null when there was no team id to read. Carried out because the quorum engine
/// needs <see cref="Models.Financia.Team.ApprovalQuorum"/> off the very same row and would otherwise
/// read it a second time.
/// </param>
/// <param name="ActiveManagers">
/// The union of [dbo].[TeamManagers] and the deprecated Teams.ManagerUserId, de-duplicated and
/// intersected with the tenant's active roster. Empty means nobody specific is responsible.
/// </param>
/// <param name="Roster">
/// The active-roster snapshot the intersection was computed against, carried out so the notifier's
/// escalation ladder and the quorum engine's eligibility filter both run over the SAME snapshot
/// without a second query.
/// </param>
internal sealed record TeamManagerSet(
    Team?               Team,
    IReadOnlyList<User> ActiveManagers,
    IReadOnlyList<User> Roster);
