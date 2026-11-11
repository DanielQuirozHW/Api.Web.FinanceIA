// Project : Api.Web.FinanceIA
// Created : 2026-10-21

namespace Api.Web.FinanceIA.Clases.Exceptions;

/// <summary>
/// Thrown by <see cref="IUserInvitationService.InviteUserAsync"/> when the address cannot be
/// invited into the tenant: it already has a user there, or a live invitation there.
/// <see cref="SettingsController"/> answers it with 409 and <see cref="ErrorCode"/>; the message is
/// Spanish and safe to show the admin.
/// </summary>
/// <remarks>
/// Derives from <see cref="InvalidOperationException"/> so the interface's "throws
/// InvalidOperationException for validation failures" contract still holds; a handler that wants
/// the 409 must catch this type BEFORE the base one.
/// </remarks>
public sealed class InvitationConflictException : InvalidOperationException
{
    /// <summary>A live invitation (unused, unrevoked, unexpired) of this tenant exists for the address.</summary>
    public const string InvitationPending = "invitation_pending";

    /// <summary>The address already belongs to a user of this tenant.</summary>
    public const string UserAlreadyExists = "user_already_exists";

    /// <param name="errorCode">One of the constants on this class.</param>
    /// <param name="message">Spanish detail, safe to send to the client.</param>
    public InvitationConflictException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>Stable machine-readable code for the Portal: <see cref="InvitationPending"/> or <see cref="UserAlreadyExists"/>.</summary>
    public string ErrorCode { get; }

    /// <summary>The conflict for an address that already has a user in the tenant.</summary>
    public static InvitationConflictException ForExistingUser() => new(
        UserAlreadyExists,
        "Ese correo ya tiene un usuario en esta organización.");

    /// <summary>The conflict for an address that already has a live invitation in the tenant.</summary>
    public static InvitationConflictException ForPendingInvitation() => new(
        InvitationPending,
        "Ya hay una invitación pendiente para este correo. Reenviala o revocala desde Invitaciones pendientes.");
}
