// Project : Api.Web.FinanceIA
// Created : 2026-09-10

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// Per-tenant maintenance of [dbo].[Teams] (DDL 062) — the analyst grouping that gives an escalated
/// analysis specific people to go to.
///
/// A team is a name, a set of MANAGERS ([dbo].[TeamManagers], DDL 064) and an APPROVAL QUORUM
/// (DDL 065), and those two are the point: when an analyst pre-approves an analysis above their
/// limit, <see cref="IAnalysisDecisionNotifier"/> notifies every manager of their team rather than
/// every RiskManager in the tenant, and <c>AnalysisApprovalQuorumService</c> holds the analysis until
/// the quorum's worth of signatures has arrived.
///
/// <para>
/// Managers are NOT role-filtered here — any active user of the tenant may be named, because the
/// notifier's job is to reach whoever the Admin says is responsible. Eligibility to actually sign is
/// a separate question, answered by <c>Roles.IsConfirmer</c> where the quorum is sized.
/// </para>
///
/// MUTATIONS are Admin only. Who reports to whom is an organisational decision with a direct effect
/// on where approvals are routed, so creating, renaming and deleting a team sits alongside the other
/// user-administration surfaces rather than with the analyst-facing endpoints.
///
/// The LISTING is open to Analyst, RiskManager and Admin (<see cref="Roles.TeamReaders"/>): both of
/// the other two roles live inside the routing structure this table describes — a RiskManager has to
/// see which teams escalate to them, an analyst which team they are in and who their manager is —
/// and reading the roster moves nothing. Auditor stays out: it does not participate in the routing.
///
/// A tenant with no teams is a supported, working state — every analyst simply falls back to the
/// RiskManager fan-out — so nothing here is required before the decision flow works.
/// </summary>
[ApiController]
[Route("api/teams")]
[Route("api/bff/teams")]
[Authorize(AuthenticationSchemes = "Internal")]
public sealed class TeamsController : ControllerBase
{
    /// <summary>Matches [Name] NVARCHAR(200) in DDL 062.</summary>
    private const int MaxNameLength = 200;

    private readonly ITenantContextAccessor    _tenantContextAccessor;
    private readonly ITeamRepository           _teamRepository;
    private readonly IUserRepository           _userRepository;
    private readonly IAuditService             _auditService;
    private readonly ILogger<TeamsController>  _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="TeamsController"/>.
    /// </summary>
    public TeamsController(
        ITenantContextAccessor   tenantContextAccessor,
        ITeamRepository          teamRepository,
        IUserRepository          userRepository,
        IAuditService            auditService,
        ILogger<TeamsController> logger)
    {
        _tenantContextAccessor = tenantContextAccessor;
        _teamRepository        = teamRepository;
        _userRepository        = userRepository;
        _auditService          = auditService;
        _logger                = logger;
    }

    // ── GET /api/bff/teams ────────────────────────────────────────────────────────

    /// <summary>
    /// Lists every team of the tenant, with its managers, its quorum and its active member count.
    /// </summary>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with the teams, ordered by name.</returns>
    /// <remarks>
    /// THREE queries in total regardless of how many teams there are: the teams, the tenant's whole
    /// manager set in one batch, and the tenant's active-user roster. Manager names and member counts
    /// are both derived from that one roster in memory. The batch read is the reason
    /// <see cref="ITeamRepository.GetManagerUserIdsByTenantAsync"/> exists — the single-team read in a
    /// loop would make this endpoint's cost grow with the number of teams.
    /// </remarks>
    [HttpGet]
    [Authorize(Roles = Roles.TeamReaders)]
    [ProducesResponseType(typeof(IReadOnlyList<TeamDto>), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetTeamsAsync(CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var teams = await _teamRepository
            .GetByTenantAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        // ONE query for the whole tenant's manager rows, not one per team.
        var managersByTeam = await _teamRepository
            .GetManagerUserIdsByTenantAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        var roster = await _userRepository
            .GetActiveByTenantAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        // TryAdd rather than ToDictionary, following TeamManagerResolution: a roster carrying the same
        // user twice is odd data but nothing forbids it, and ToDictionary would throw on the duplicate
        // key — turning a listing into a 500 over a row that harms nothing.
        var namesByUserId = new Dictionary<Guid, string>();
        foreach (var user in roster) namesByUserId.TryAdd(user.UserId, user.DisplayName);

        var memberCounts = roster
            .Where(u => u.TeamId is not null)
            .GroupBy(u => u.TeamId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var response = teams.Select(team =>
        {
            var managerIds = ResolveManagerIds(team, managersByTeam);

            return new TeamDto
            {
                TeamId        = team.TeamId,
                Name          = team.Name,
                // The deprecated singular pair, kept in step with the set so a Portal build that
                // predates the multi-manager drawer keeps rendering.
                ManagerUserId = managerIds.Count > 0 ? managerIds[0] : null,
                // Null when no manager is named OR when their account is inactive — the roster only
                // holds active users, which is the same set the notifier will accept as a manager.
                ManagerName   = managerIds.Count > 0
                                && namesByUserId.TryGetValue(managerIds[0], out var managerName)
                                    ? managerName
                                    : null,
                Managers      = BuildManagerDtos(managerIds, namesByUserId),
                MemberCount   = memberCounts.TryGetValue(team.TeamId, out var count) ? count : 0,
                // Omitting it here would serve approvalQuorum: 0 from the LIST while the single-team
                // responses served the real value — a difference the Portal would see.
                ApprovalQuorum = team.ApprovalQuorum,
                CreatedAt     = team.CreatedAt,
                UpdatedAt     = team.UpdatedAt
            };
        }).ToList();

        return Ok(response);
    }

    // ── POST /api/bff/teams ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates a team.
    /// </summary>
    /// <param name="request">The team to create.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>201 Created with the new team, or 400/404.</returns>
    [HttpPost]
    [Authorize(Roles = Roles.Admin)]
    [ProducesResponseType(typeof(TeamDto), 201)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> CreateTeamAsync(
        [FromBody] CreateTeamRequest request,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var currentUser = await ResolveCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (currentUser is null) return Unauthorized();

        var (name, nameError) = ValidateName(request?.Name);
        if (nameError is not null) return nameError;

        var quorumError = ValidateQuorum(request!.ApprovalQuorum);
        if (quorumError is not null) return quorumError;

        // EVERYTHING is validated before the first write, so a bad id leaves nothing behind.
        var (managerIds, roster, managerError) = await ResolveManagerSetAsync(
            request.ManagerUserIds, request.ManagerUserId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (managerError is not null) return managerError;

        var now = DateTime.UtcNow;

        var team = new Team
        {
            // TeamConfiguration maps TeamId as ValueGeneratedNever — the app supplies it.
            TeamId        = Guid.NewGuid(),
            TenantId      = tenantId,
            Name          = name,
            // The deprecated mirror: the FIRST of the set, so the union read in
            // TeamManagerResolution still resolves someone on a tenant whose DDL 066 never ran.
            ManagerUserId = managerIds.Count > 0 ? managerIds[0] : null,
            // Omitted means 1 on CREATE — the DDL 065 default, and today's one-signature behaviour.
            ApprovalQuorum = request.ApprovalQuorum ?? 1,
            CreatedAt     = now,
            CreatedBy     = currentUser.UserId,
            UpdatedAt     = now,
            UpdatedBy     = currentUser.UserId
        };

        await _teamRepository.CreateAsync(team, cancellationToken).ConfigureAwait(false);

        // [dbo].[TeamManagers] (DDL 064) is the SOURCE OF TRUTH the notifier and the quorum engine
        // both read; Teams.ManagerUserId above is only the deprecated mirror. Writing the whole set
        // here is what supersedes the single-manager mirror this endpoint used to do.
        await _teamRepository
            .ReplaceManagersAsync(team.TeamId, tenantId, managerIds, currentUser.UserId, cancellationToken)
            .ConfigureAwait(false);

        _ = _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       currentUser.UserId,
            action:       "Team.Created",
            severity:     "Info",
            entityType:   "Team",
            entityId:     team.TeamId.ToString(),
            ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:    HttpContext.TraceIdentifier,
            oldValueJson: null,
            newValueJson: null,
            ct:           CancellationToken.None);

        _logger.LogInformation(
            "Team created: {TeamId} tenant {TenantId} by {UserId}",
            team.TeamId, tenantId, currentUser.UserId);

        // The Location is written directly rather than generated from an action name: there is no
        // GET-by-id route for a team, and CreatedAtAction resolves the URL at execution time — AFTER
        // the row is committed — so a miss there turns a successful insert into a 500.
        // The roster and the manager set are both already in hand, so the DTO costs no further query.
        var dto = BuildDto(team, managerIds, roster);

        return Created($"/api/teams/{team.TeamId}", dto);
    }

    // ── PUT /api/bff/teams/{id} ───────────────────────────────────────────────────

    /// <summary>
    /// Updates a team's name, manager set and approval quorum.
    /// </summary>
    /// <param name="id">The team to update.</param>
    /// <param name="request">The new values. The name and the manager set are replaced.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with the updated team, or 400/404.</returns>
    /// <remarks>
    /// A full replace of the name and the manager set, not a patch: the Admin screen edits the whole
    /// team at once and "remove every manager" has to be expressible, which an empty
    /// <c>managerUserIds</c> is.
    ///
    /// <para>
    /// <c>approvalQuorum</c> is the exception — omitting it preserves the stored value. See the
    /// remarks on <see cref="UpdateTeamRequest"/>.
    /// </para>
    /// </remarks>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = Roles.Admin)]
    [ProducesResponseType(typeof(TeamDto), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> UpdateTeamAsync(
        Guid id,
        [FromBody] UpdateTeamRequest request,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var currentUser = await ResolveCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (currentUser is null) return Unauthorized();

        var (name, nameError) = ValidateName(request?.Name);
        if (nameError is not null) return nameError;

        var quorumError = ValidateQuorum(request!.ApprovalQuorum);
        if (quorumError is not null) return quorumError;

        var team = await _teamRepository
            .GetByIdAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (team is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el equipo.");

        // Validated BEFORE UpdateAsync, so a bad id in the array leaves the team exactly as it was.
        var (managerIds, roster, managerError) = await ResolveManagerSetAsync(
            request.ManagerUserIds, request.ManagerUserId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (managerError is not null) return managerError;

        team.Name          = name;
        team.ManagerUserId = managerIds.Count > 0 ? managerIds[0] : null;
        team.UpdatedAt     = DateTime.UtcNow;
        team.UpdatedBy     = currentUser.UserId;

        // THE ONE FIELD THAT IS NOT A FULL REPLACE. An omitted quorum PRESERVES the stored value
        // instead of resetting it to 1: a Portal build that predates this field sends every other
        // field and no quorum, and resetting would silently drop a configured quorum of 2 back to 1
        // on a plain rename — weakening a credit control with nothing in the UI to show for it.
        if (request.ApprovalQuorum is int quorum) team.ApprovalQuorum = quorum;

        await _teamRepository.UpdateAsync(team, cancellationToken).ConfigureAwait(false);

        // The manager SET is a full replace: whatever was there is gone, and an empty list correctly
        // empties the team rather than leaving a stale row behind that the notifier would still act on.
        await _teamRepository
            .ReplaceManagersAsync(team.TeamId, tenantId, managerIds, currentUser.UserId, cancellationToken)
            .ConfigureAwait(false);

        _ = _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       currentUser.UserId,
            action:       "Team.Updated",
            severity:     "Info",
            entityType:   "Team",
            entityId:     team.TeamId.ToString(),
            ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:    HttpContext.TraceIdentifier,
            oldValueJson: null,
            newValueJson: null,
            ct:           CancellationToken.None);

        _logger.LogInformation(
            "Team updated: {TeamId} tenant {TenantId} by {UserId}",
            team.TeamId, tenantId, currentUser.UserId);

        return Ok(BuildDto(team, managerIds, roster));
    }

    // ── DELETE /api/bff/teams/{id} ────────────────────────────────────────────────

    /// <summary>
    /// Deletes a team, unassigning its members first.
    /// </summary>
    /// <param name="id">The team to delete.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>204 No Content, or 404.</returns>
    /// <remarks>
    /// The members are unassigned BEFORE the row is removed. FK_Users_Team has NO CASCADE
    /// deliberately — a cascade would delete the analysts along with their team — so without this
    /// the delete would simply be rejected by the database.
    ///
    /// <para>
    /// The members themselves are untouched apart from losing the assignment; they fall back to the
    /// RiskManager fan-out, which is the same state they were in before any team existed.
    /// </para>
    /// </remarks>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = Roles.Admin)]
    [ProducesResponseType(204)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> DeleteTeamAsync(Guid id, CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var currentUser = await ResolveCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (currentUser is null) return Unauthorized();

        var team = await _teamRepository
            .GetByIdAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (team is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el equipo.");

        var cleared = await _userRepository
            .ClearTeamAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        // And clear the managers, for the same reason and with the same urgency: FK_TeamManagers_Team
        // (DDL 064) has no cascade either, so a team with manager rows cannot be deleted while they
        // exist. A team delete now has to satisfy BOTH constraints — FK_Users_Team above and this
        // one — or it fails at the database with a 500. An empty set is the supported way to clear.
        await _teamRepository
            .ReplaceManagersAsync(id, tenantId, [], currentUser.UserId, cancellationToken)
            .ConfigureAwait(false);

        var deleted = await _teamRepository
            .DeleteAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (!deleted)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el equipo.");

        _ = _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       currentUser.UserId,
            action:       "Team.Deleted",
            severity:     "Info",
            entityType:   "Team",
            entityId:     id.ToString(),
            ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:    HttpContext.TraceIdentifier,
            oldValueJson: null,
            newValueJson: null,
            ct:           CancellationToken.None);

        _logger.LogInformation(
            "Team deleted: {TeamId} tenant {TenantId} by {UserId}, {ClearedCount} member(s) unassigned",
            id, tenantId, currentUser.UserId, cleared);

        return NoContent();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds one team's DTO from a manager set and a roster the caller already holds.
    /// </summary>
    /// <remarks>
    /// Takes both rather than re-reading them: the create and update paths have just validated the
    /// manager ids against the roster, so fetching either again would be a query for data in hand.
    /// </remarks>
    private static TeamDto BuildDto(Team team, IReadOnlyList<Guid> managerIds, IReadOnlyList<User> roster)
    {
        var namesByUserId = new Dictionary<Guid, string>();
        foreach (var user in roster) namesByUserId.TryAdd(user.UserId, user.DisplayName);

        return new TeamDto
        {
            TeamId        = team.TeamId,
            Name          = team.Name,
            // The deprecated singular pair, mirroring the first of the set exactly as the stored
            // Teams.ManagerUserId column now does.
            ManagerUserId = managerIds.Count > 0 ? managerIds[0] : null,
            ManagerName   = managerIds.Count > 0
                            && namesByUserId.TryGetValue(managerIds[0], out var managerName)
                                ? managerName
                                : null,
            Managers      = BuildManagerDtos(managerIds, namesByUserId),
            MemberCount   = roster.Count(u => u.TeamId == team.TeamId),
            ApprovalQuorum = team.ApprovalQuorum,
            CreatedAt     = team.CreatedAt,
            UpdatedAt     = team.UpdatedAt
        };
    }

    /// <summary>
    /// Pairs each manager id with the display name from the active roster, or null when there is none.
    /// </summary>
    /// <remarks>
    /// A missing name is NOT a missing manager. The roster holds active users only, so an account
    /// deactivated after being named resolves to null here — and the id is still returned, because it
    /// is what the row stores and what an Admin has to see in order to remove it.
    /// </remarks>
    private static List<TeamManagerDto> BuildManagerDtos(
        IReadOnlyList<Guid> managerIds, IReadOnlyDictionary<Guid, string> namesByUserId)
        => managerIds
            .Select(managerId => new TeamManagerDto
            {
                UserId      = managerId,
                DisplayName = namesByUserId.TryGetValue(managerId, out var name) ? name : null
            })
            .ToList();

    /// <summary>
    /// The manager ids of one team as the LIST endpoint should report them: [dbo].[TeamManagers]
    /// unioned with the deprecated <see cref="Team.ManagerUserId"/>, de-duplicated.
    /// </summary>
    /// <remarks>
    /// The same union <c>TeamManagerResolution</c> performs, and for the same reason: DDL 066 is a
    /// DATA script while the <c>--verify-schema</c> gate compares columns, so a tenant whose 066 was
    /// skipped still has its manager only in the column. Reading both means the Admin screen shows
    /// the manager the notifier would actually reach rather than an empty list.
    /// </remarks>
    private static List<Guid> ResolveManagerIds(
        Team team, IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> managersByTeam)
    {
        var ids = new List<Guid>();
        var seen = new HashSet<Guid>();

        if (managersByTeam.TryGetValue(team.TeamId, out var stored))
        {
            foreach (var managerId in stored)
            {
                if (seen.Add(managerId)) ids.Add(managerId);
            }
        }

        if (team.ManagerUserId is Guid legacyManagerId && seen.Add(legacyManagerId))
            ids.Add(legacyManagerId);

        return ids;
    }

    /// <summary>
    /// Trims and bounds a team name.
    /// </summary>
    private (string name, IActionResult? error) ValidateName(string? rawName)
    {
        var name = rawName?.Trim() ?? string.Empty;

        if (name.Length == 0)
        {
            return (string.Empty, Problem(
                statusCode: 400, title: "Bad Request",
                detail: "El nombre del equipo es obligatorio."));
        }

        if (name.Length > MaxNameLength)
        {
            return (string.Empty, Problem(
                statusCode: 400, title: "Bad Request",
                detail: $"El nombre del equipo no puede superar los {MaxNameLength} caracteres."));
        }

        return (name, null);
    }

    /// <summary>
    /// Bounds the approval quorum. Null (omitted) is always accepted — what that MEANS differs
    /// between create and update, and is decided by the caller.
    /// </summary>
    /// <remarks>
    /// A LOWER BOUND ONLY. DDL 065 deliberately ships no CHECK constraint, so 1 is enforced here or
    /// nowhere: a 0 would mean an analysis confirmed on no signatures at all, which is why
    /// <c>AnalysisApprovalQuorumService</c> floors the stored value defensively as well.
    ///
    /// <para>
    /// There is NO upper bound, and specifically a quorum larger than the team's manager count is
    /// accepted. The engine applies <c>MIN(quorum, eligible active managers)</c>, so a 3 on a
    /// two-manager team is legal and settles on two signatures rather than stranding the analysis.
    /// Validating against the manager count instead would make the valid range depend on a set that
    /// changes independently — deactivate a manager and a previously valid team becomes unsaveable.
    /// </para>
    /// </remarks>
    private IActionResult? ValidateQuorum(int? approvalQuorum)
    {
        if (approvalQuorum is int quorum && quorum < 1)
        {
            return Problem(
                statusCode: 400, title: "Bad Request",
                detail: "El quórum de aprobación debe ser al menos 1.");
        }

        return null;
    }

    /// <summary>
    /// Resolves the requested manager set, checking every id is an active user of this tenant.
    /// </summary>
    /// <param name="managerUserIds">The array form. Wins whenever it is present, EMPTY INCLUDED.</param>
    /// <param name="legacyManagerUserId">
    /// The deprecated singular field, used only when <paramref name="managerUserIds"/> is absent.
    /// </param>
    /// <param name="tenantId">Tenant scope.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <returns>
    /// The de-duplicated ids in the order they were sent, the active roster they were checked against
    /// (carried out so the caller can build the DTO without a second query), and a 400 when any id is
    /// not an active user of this tenant.
    /// </returns>
    /// <remarks>
    /// PRECEDENCE. An array that is present — even empty — is the whole truth, because "no managers"
    /// has to be expressible and an empty array is how the Portal will express it. Only a wholly
    /// absent array falls back to the singular field, which is what keeps a Portal build that predates
    /// the multi-manager drawer working for one release.
    ///
    /// <para>
    /// VALIDATED AGAINST THE ROSTER, not with a lookup per id. <c>GetActiveByTenantAsync</c> filters
    /// on exactly <c>TenantId == tenantId &amp;&amp; IsActive</c>, so membership in it is the same
    /// three checks the single-manager validation used to make one at a time — while costing one query
    /// instead of one per id, which matters because the array's length is the caller's choice.
    /// </para>
    ///
    /// <para>
    /// Tenant scope is part of that check rather than left to the repository because user ids are
    /// globally unique: without it an Admin could name a user of another tenant by guessing a GUID.
    /// Deliberately 400 and not 404 — the manager is a field of the request being validated, not the
    /// resource being addressed.
    /// </para>
    ///
    /// <para>
    /// An INACTIVE user is refused too. The notifier only accepts an active manager and the quorum
    /// engine only counts one, so allowing an inactive manager here would let an Admin configure a
    /// routing that quietly does not work.
    /// </para>
    /// </remarks>
    private async Task<(List<Guid> ManagerIds, IReadOnlyList<User> Roster, IActionResult? Error)>
        ResolveManagerSetAsync(
            List<Guid>?       managerUserIds,
            Guid?             legacyManagerUserId,
            string            tenantId,
            CancellationToken ct)
    {
        var requested = managerUserIds
            ?? (legacyManagerUserId is Guid legacyId ? [legacyId] : new List<Guid>());

        var roster = await _userRepository
            .GetActiveByTenantAsync(tenantId, ct)
            .ConfigureAwait(false);

        var activeUserIds = roster.Select(u => u.UserId).ToHashSet();

        // De-duplicated preserving order. ReplaceManagersAsync collapses duplicates itself, but the
        // order matters here: the FIRST id becomes the deprecated Teams.ManagerUserId mirror, so it
        // has to be stable and it has to survive de-duplication.
        var managerIds = new List<Guid>();
        var seen = new HashSet<Guid>();

        foreach (var managerId in requested)
        {
            // Guid.Empty can never be in the roster, but rejecting it explicitly says so rather than
            // leaving it to look like a user who happens not to exist.
            if (managerId == Guid.Empty || !activeUserIds.Contains(managerId))
            {
                return ([], roster, Problem(
                    statusCode: 400, title: "Bad Request",
                    detail: "El responsable indicado no es un usuario activo de este tenant."));
            }

            if (seen.Add(managerId)) managerIds.Add(managerId);
        }

        return (managerIds, roster, null);
    }

    /// <summary>
    /// Resolves the tenant from the request's <see cref="TenantContext"/>.
    /// </summary>
    private (string tenantId, IActionResult? error) ResolveTenant()
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in TeamsController");
            return (string.Empty, Problem(
                statusCode: 500, title: "Internal Server Error",
                detail: "Tenant context not resolved."));
        }

        return (tenantContext.TenantId, null);
    }

    /// <summary>
    /// Resolves the calling user from the JWT, following AnalysisController's helper.
    /// </summary>
    private async Task<User?> ResolveCurrentUserAsync(CancellationToken ct)
    {
        var firebaseUid = User.FindFirst("uid")?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrWhiteSpace(firebaseUid))
            return null;

        return await _userRepository.GetByFirebaseUidAsync(firebaseUid, ct).ConfigureAwait(false);
    }
}
