// Project : Api.Web.FinanceIA
// Created : 2026-07-24

namespace Api.Web.FinanceIA.Models.Settings;

/// <summary>
/// Read-only projection of a tenant invitation for the admin invitations list.
/// <c>Status</c> is derived (never stored) — see InvitationStatusResolver.
/// </summary>
public sealed record InvitationDto
{
    public required Guid InvitationId { get; init; }
    public required string Email { get; init; }
    public required string Role { get; init; }

    /// <summary>Derived lifecycle status: pending | accepted | expired | revoked.</summary>
    public required string Status { get; init; }

    /// <summary>
    /// The inviting admin, or null for a first-admin bootstrap invitation (DDL 075), which has none.
    /// The Portal never reads this field; it renders <see cref="InvitedByName"/>.
    /// </summary>
    public required Guid? InvitedByUserId { get; init; }

    /// <summary>
    /// Display name of the inviting admin; null when it cannot be resolved. For a bootstrap
    /// invitation it is the neutral label "Configuración inicial".
    /// </summary>
    public string? InvitedByName { get; init; }

    public required DateTime CreatedAt { get; init; }
    public required DateTime ExpiresAt { get; init; }
    public DateTime? UsedAt { get; init; }
    public DateTime? RevokedAt { get; init; }
}
