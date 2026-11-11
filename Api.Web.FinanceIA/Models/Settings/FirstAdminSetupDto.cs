// Project : Api.Web.FinanceIA
// Created : 2026-10-25

namespace Api.Web.FinanceIA.Models.Settings;

/// <summary>
/// Body of POST /api/bff/setup/first-admin. <c>Email</c> is deliberately not <c>required</c>: the
/// action validates it itself, so a malformed body answers the same Spanish 400 as the normal invite
/// instead of the framework's automatic one.
/// </summary>
public sealed record FirstAdminSetupRequest
{
    public string? Email { get; init; }
}

/// <summary>
/// 201 body of POST /api/bff/setup/first-admin. <c>InviteUrl</c> is exactly the link the invitation
/// email carries, so the operator can hand it over when the email could not be sent
/// (<c>EmailSent</c> false). It grants Admin until used, revoked or expired: treat it as a secret.
/// </summary>
public sealed record FirstAdminInvitationResponse
{
    public required Guid InvitationId { get; init; }
    public required string Email { get; init; }
    public required DateTime ExpiresAt { get; init; }
    public required string InviteUrl { get; init; }
    public required bool EmailSent { get; init; }
}

/// <summary>
/// Result of <c>IUserInvitationService.InviteFirstAdminAsync</c>: either the tenant already has an
/// active Admin (nothing was created) or the invitation that was.
/// </summary>
public sealed record FirstAdminInviteOutcome
{
    /// <summary>The outcome when the tenant already has an active Admin.</summary>
    public static FirstAdminInviteOutcome AdminExists { get; } = new() { AdminAlreadyExists = true };

    public required bool AdminAlreadyExists { get; init; }

    /// <summary>The created invitation; null exactly when <see cref="AdminAlreadyExists"/> is true.</summary>
    public FirstAdminInvitationResponse? Invitation { get; init; }
}
