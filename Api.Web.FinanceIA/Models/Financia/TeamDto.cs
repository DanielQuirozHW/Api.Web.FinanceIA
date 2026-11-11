// Project : Api.Web.FinanceIA
// Created : 2026-09-10

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// DTO for a single team returned by the teams endpoints.
/// </summary>
/// <remarks>
/// Serialised in camelCase (teamId, managerUserId, memberCount, ...): Program.cs configures
/// AddJsonOptions without a PropertyNamingPolicy, so the ASP.NET Core web default applies — the same
/// casing every other controller in this API emits.
///
/// <para>
/// TenantId is deliberately absent, as on <see cref="NotificationDto"/>: it is implicit in the
/// caller's own tenant context and there is no reason to echo it back.
/// </para>
/// </remarks>
public sealed class TeamDto
{
    public Guid TeamId { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// DEPRECATED mirror of the FIRST entry of <see cref="Managers"/>. Null when the team has no
    /// managers.
    /// </summary>
    /// <remarks>
    /// Kept only so a Portal build that predates the multi-manager drawer keeps rendering, and so the
    /// union read in <c>TeamManagerResolution</c> still resolves someone on a tenant whose DDL 066
    /// data migration never ran. Read <see cref="Managers"/> instead: a team routes to ALL of them,
    /// and this field shows one. The Phase-3 cleanup removes it.
    /// </remarks>
    public Guid? ManagerUserId { get; init; }

    /// <summary>
    /// DEPRECATED. Display name of <see cref="ManagerUserId"/>, or null when there is no manager or
    /// their account is inactive. Superseded by <see cref="Managers"/>.
    /// </summary>
    public string? ManagerName { get; init; }

    /// <summary>
    /// EVERY manager of this team, in the order they were stored, with their display name resolved.
    /// Empty when nobody has been named — the pre-approval then falls back to the RiskManager fan-out.
    /// </summary>
    /// <remarks>
    /// The list the Portal's team drawer edits, and the set <c>AnalysisDecisionNotifier</c> actually
    /// notifies. <see cref="TeamManagerDto.DisplayName"/> is null when the account was deactivated
    /// AFTER being named: the id stays because it is what the row stores, but the name cannot be
    /// resolved from the active roster and such a manager no longer counts toward the quorum.
    /// </remarks>
    public IReadOnlyList<TeamManagerDto> Managers { get; init; } = [];

    /// <summary>
    /// How many ACTIVE users belong to this team. Deactivated accounts are excluded, matching the
    /// roster the count is derived from.
    /// </summary>
    public int MemberCount { get; init; }

    /// <summary>
    /// How many manager signatures a pre-approval from this team needs before it is confirmed
    /// (DDL 065). At least 1.
    /// </summary>
    /// <remarks>
    /// WRITABLE since this release — <see cref="CreateTeamRequest"/> and
    /// <see cref="UpdateTeamRequest"/> both carry it — and ENFORCED since Phase 2, by
    /// <c>AnalysisApprovalQuorumService</c>.
    ///
    /// <para>
    /// This is the CONFIGURED value, not the effective one. The engine applies
    /// <c>MIN(quorum, eligible active managers)</c>, so a team showing 3 with two managers who can
    /// confirm settles on two signatures. See <c>AnalysisApprovalQuorumService.ResolveEffectiveQuorumAsync</c>.
    /// </para>
    /// </remarks>
    public int ApprovalQuorum { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }
}

/// <summary>
/// One manager of a team, as returned inside <see cref="TeamDto.Managers"/>.
/// </summary>
public sealed record TeamManagerDto
{
    /// <summary>The manager's UserId. Always present — it is what [dbo].[TeamManagers] stores.</summary>
    public required Guid UserId { get; init; }

    /// <summary>
    /// The manager's display name, or null when their account has since been deactivated and they are
    /// therefore absent from the active roster this was resolved against.
    /// </summary>
    public string? DisplayName { get; init; }
}

/// <summary>
/// Request body for creating a team.
/// </summary>
public sealed record CreateTeamRequest
{
    /// <summary>Display name. Required; trimmed and length-checked by the controller.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// DEPRECATED single manager, accepted for ONE release so a Portal build that predates the
    /// multi-manager drawer keeps working. Ignored whenever <see cref="ManagerUserIds"/> is present.
    /// </summary>
    public Guid? ManagerUserId { get; init; }

    /// <summary>
    /// The team's managers. Every id must be an active user of the caller's tenant; duplicates are
    /// collapsed. An empty list creates a team with no manager, which is a supported state.
    /// </summary>
    /// <remarks>
    /// TAKES PRECEDENCE over <see cref="ManagerUserId"/> whenever it is present — including when it is
    /// present and EMPTY, which means "no managers" rather than "fall back to the singular field".
    /// Only a wholly absent <c>managerUserIds</c> falls back, as a 0/1-element set. The Portal sends
    /// the array once Frente 2 ships; the fallback exists so the two deploys need not be simultaneous.
    /// </remarks>
    public List<Guid>? ManagerUserIds { get; init; }

    /// <summary>
    /// How many manager signatures a pre-approval from this team needs. Must be at least 1.
    /// Defaults to 1 when omitted.
    /// </summary>
    /// <remarks>
    /// A quorum LARGER than the number of managers is accepted on purpose: the engine applies
    /// <c>MIN(quorum, eligible active managers)</c>, so a 3 on a two-manager team is legal and simply
    /// behaves as 2. Rejecting it here would make the valid range depend on the manager set and would
    /// break the moment a manager is deactivated.
    /// </remarks>
    public int? ApprovalQuorum { get; init; }
}

/// <summary>
/// Request body for updating a team.
/// </summary>
/// <remarks>
/// A full replace of the name and the manager set rather than a patch: the Admin screen edits the whole
/// team at once, and "remove every manager" has to be expressible — which is what an empty
/// <see cref="ManagerUserIds"/> means.
///
/// <para>
/// <see cref="ApprovalQuorum"/> is THE ONE EXCEPTION to that rule: omitting it preserves the stored
/// value instead of resetting it to 1. A Portal build that predates this field sends every other field
/// and no quorum, and under strict full-replace a plain rename would silently drop a configured quorum
/// of 2 back to 1 — weakening a credit control with nothing in the UI to show for it.
/// </para>
/// </remarks>
public sealed record UpdateTeamRequest
{
    /// <summary>Display name. Required; trimmed and length-checked by the controller.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// DEPRECATED single manager, accepted for ONE release. Ignored whenever
    /// <see cref="ManagerUserIds"/> is present; null with no array clears the team's managers.
    /// </summary>
    public Guid? ManagerUserId { get; init; }

    /// <summary>
    /// The team's new complete manager set — whatever was there before is replaced. Every id must be
    /// an active user of the caller's tenant; duplicates are collapsed. An empty list clears them.
    /// </summary>
    /// <remarks>
    /// Same precedence as on <see cref="CreateTeamRequest.ManagerUserIds"/>: present (even empty) wins
    /// over the singular field; wholly absent falls back to it as a 0/1-element set.
    /// </remarks>
    public List<Guid>? ManagerUserIds { get; init; }

    /// <summary>
    /// The new quorum, at least 1. OMITTED LEAVES THE STORED VALUE UNCHANGED — see the remarks on this
    /// record for why this one field is not a full replace.
    /// </summary>
    /// <remarks>
    /// As on create, a quorum larger than the manager count is accepted: the engine caps the effective
    /// quorum at the eligible-manager count.
    /// </remarks>
    public int? ApprovalQuorum { get; init; }
}

/// <summary>
/// Request body for assigning a user to a team.
/// </summary>
public sealed record SetUserTeamRequest
{
    /// <summary>The team to assign the user to, or null to unassign them.</summary>
    public Guid? TeamId { get; init; }
}
