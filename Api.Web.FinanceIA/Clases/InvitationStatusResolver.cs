// Project : Api.Web.FinanceIA
// Created : 2026-07-24

using Api.Web.FinanceIA.Models.Entities;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Single source of truth for deriving an invitation's lifecycle status.
/// Status is COMPUTED, never stored. Precedence: accepted -> revoked -> expired -> pending.
/// Accepted outranks revoked because a used invitation can never be revoked
/// (enforced on the write path), so the two are mutually exclusive.
/// </summary>
public static class InvitationStatusResolver
{
    /// <summary>The invitation is still open and can be accepted.</summary>
    public const string Pending = "pending";

    /// <summary>The invitation was accepted and the user registered.</summary>
    public const string Accepted = "accepted";

    /// <summary>The invitation lapsed without being accepted.</summary>
    public const string Expired = "expired";

    /// <summary>The invitation was cancelled by an admin.</summary>
    public const string Revoked = "revoked";

    /// <summary>
    /// Derives the status of <paramref name="invitation"/> at <paramref name="utcNow"/>.
    /// </summary>
    /// <param name="invitation">The invitation to evaluate.</param>
    /// <param name="utcNow">The current UTC time to compare expiry against.</param>
    public static string Resolve(UserInvitation invitation, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(invitation);

        if (invitation.UsedAt is not null)
            return Accepted;

        if (invitation.RevokedAt is not null)
            return Revoked;

        if (invitation.ExpiresAt < utcNow)
            return Expired;

        return Pending;
    }
}
