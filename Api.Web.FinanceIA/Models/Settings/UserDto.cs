// Project : Api.Web.FinanceIA
// Created : 2026-05-11

namespace Api.Web.FinanceIA.Models.Settings;

/// <summary>
/// Read-only projection of a tenant user for the admin user list.
/// </summary>
public sealed record UserDto
{
    public required Guid UserId { get; init; }
    public required string DisplayName { get; init; }
    public required string Email { get; init; }
    public required string Role { get; init; }
    public required bool IsActive { get; init; }
    public DateTime? LastLoginAt { get; init; }

    /// <summary>
    /// Derived invitation status. A user that exists in the users table has, by
    /// definition, completed registration — so this is always "accepted". Pending
    /// invitations are a separate collection (GET /api/bff/settings/invitations).
    /// </summary>
    public required string InvitationStatus { get; init; }

    /// <summary>
    /// Team this user belongs to (DDL 063), or null when they are not assigned to one.
    /// </summary>
    public Guid? TeamId { get; init; }

    /// <summary>
    /// Name of <see cref="TeamId"/>'s team. Null when the user has no team, and also when the id
    /// no longer names a team of this tenant — a team deleted concurrently degrades to no name
    /// rather than failing the whole list.
    /// </summary>
    public string? TeamName { get; init; }
}

/// <summary>
/// Payload for inviting a new user to the tenant.
/// </summary>
public sealed record InviteUserSettingsRequest
{
    public required string Email { get; init; }
    public required string Role { get; init; }
}

/// <summary>
/// Response returned after successfully creating a user invitation.
/// </summary>
public sealed record InviteUserResponse
{
    public required Guid InvitationId { get; init; }
    public required string Email { get; init; }
    public required DateTime ExpiresAt { get; init; }
    public required string InvitationLink { get; init; }
}
