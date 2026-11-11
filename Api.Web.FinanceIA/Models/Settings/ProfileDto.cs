// Project : Api.Web.FinanceIA
// Created : 2026-05-11

namespace Api.Web.FinanceIA.Models.Settings;

/// <summary>
/// Read-only projection of a user profile for the settings screen.
/// </summary>
public sealed record ProfileDto
{
    public required Guid UserId { get; init; }
    public required string DisplayName { get; init; }
    public required string Email { get; init; }
    public required string Role { get; init; }
    public DateTime? LastLoginAt { get; init; }
}

/// <summary>
/// Payload for updating the authenticated user's display name.
/// </summary>
public sealed record UpdateProfileRequest
{
    public required string DisplayName { get; init; }
}
