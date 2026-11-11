// Project : Api.Web.FinanceIA
// Created : 2026-05-11

using Api.Web.FinanceIA.Models.Entities;
using Api.Web.FinanceIA.Models.Settings;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Outcome of a resend/revoke action, mapped to HTTP status by the controller.
/// </summary>
public enum InvitationActionResult
{
    /// <summary>The action succeeded (or was an idempotent no-op).</summary>
    Success,

    /// <summary>No invitation with that id exists in the tenant.</summary>
    NotFound,

    /// <summary>The invitation is in a terminal state that forbids the action.</summary>
    InvalidState
}

/// <summary>
/// Orchestrates user invitation workflows: creation, validation, email dispatch.
/// </summary>
public interface IUserInvitationService
{
    /// <summary>
    /// Creates an invitation, sends the email, and returns the invitation details.
    /// Throws <see cref="Exceptions.InvitationConflictException"/> (an
    /// <see cref="InvalidOperationException"/>) when the address already has a user in the tenant or
    /// a live invitation of the tenant — unused, unrevoked and unexpired; revoked, expired and other
    /// tenants' invitations never block. Throws <see cref="InvalidOperationException"/> for other
    /// failures (configuration, email provider).
    /// </summary>
    Task<InviteUserResponse> InviteUserAsync(
        string tenantId,
        Guid invitedByUserId,
        InviteUserSettingsRequest request,
        string? ipAddress,
        string? requestId,
        CancellationToken ct);

    /// <summary>
    /// First-admin bootstrap (POST /api/bff/setup/first-admin): creates an <c>Admin</c> invitation
    /// with NO inviter (<c>InvitedByUserId</c> null, DDL 075) and sends the same invitation email as
    /// <see cref="InviteUserAsync"/> — same token format, same 7-day expiry, same link.
    /// <list type="bullet">
    /// <item>Returns <see cref="FirstAdminInviteOutcome.AdminExists"/> and creates nothing when the
    /// tenant has an ACTIVE Admin; inactive Admins and other roles do not count.</item>
    /// <item>Revokes a live invitation for the address that an earlier bootstrap call created (no
    /// inviter) and issues a new one; a live invitation an admin sent still throws
    /// <see cref="Exceptions.InvitationConflictException"/>, as does an existing user.</item>
    /// <item>An email failure does not fail the call: the invitation is kept and
    /// <see cref="FirstAdminInvitationResponse.EmailSent"/> is false.</item>
    /// </list>
    /// Writes the audit row <c>Setup.FirstAdminInvited</c>. Never logs or audits the token or link.
    /// </summary>
    Task<FirstAdminInviteOutcome> InviteFirstAdminAsync(
        string tenantId,
        string email,
        string? ipAddress,
        string? requestId,
        CancellationToken ct);

    /// <summary>
    /// Returns the active, unexpired invitation matching the given token (any tenant),
    /// or null if not found, already used, or expired.
    /// </summary>
    Task<UserInvitation?> GetInvitationByTokenAsync(string token, CancellationToken ct);

    /// <summary>
    /// Returns all invitations for the tenant with derived status, optionally filtered
    /// by status (pending | accepted | expired | revoked). Inviter display names are
    /// resolved in batch. Ordered pending-first (soonest expiry first), then the rest
    /// by creation time desc.
    /// </summary>
    Task<IReadOnlyList<InvitationDto>> GetInvitationsAsync(
        string tenantId,
        string? status,
        CancellationToken ct);

    /// <summary>
    /// Resends a pending invitation: rotates its token, extends expiry, and re-sends
    /// the email — keeping the same <c>InvitationId</c>. Returns
    /// <see cref="InvitationActionResult.InvalidState"/> if it is already accepted or revoked.
    /// </summary>
    Task<InvitationActionResult> ResendInvitationAsync(
        string tenantId,
        Guid invitationId,
        Guid resentByUserId,
        string? ipAddress,
        string? requestId,
        CancellationToken ct);

    /// <summary>
    /// Revokes an invitation (sets RevokedAt/RevokedByUserId). Idempotent when already
    /// revoked. Returns <see cref="InvitationActionResult.InvalidState"/> if it is already accepted.
    /// </summary>
    Task<InvitationActionResult> RevokeInvitationAsync(
        string tenantId,
        Guid invitationId,
        Guid revokedByUserId,
        string? ipAddress,
        string? requestId,
        CancellationToken ct);
}
