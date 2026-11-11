// Project : Api.Web.FinanceIA
// Created : 2026-05-02
// Modified: 2026-05-02

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Response body returned by the register and me endpoints.
/// Contains the non-sensitive profile fields for the authenticated user.
/// </summary>
public sealed class UserProfileResponse
{
    /// <summary>Unique identifier of the user record.</summary>
    public Guid UserId { get; set; }

    /// <summary>User email address.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Display name shown in the UI.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Application role assigned to the user.</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>When true the user must change their password on next login.</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>Indicates whether two-factor authentication is enabled.</summary>
    public bool TwoFactorEnabled { get; set; }

    /// <summary>UTC timestamp of the most recent successful login.</summary>
    public DateTime? LastLoginAt { get; set; }
}
