// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Clases.PasswordReset;

/// <summary>
/// What happened to one queued password-reset request. Logged with the tenant and the email tag;
/// the caller never sees it — every request already got the same 202.
/// </summary>
public enum PasswordResetOutcome
{
    /// <summary>Link generated and SendGrid accepted the email.</summary>
    Sent,

    /// <summary>No user with that address in the tenant. Nothing sent.</summary>
    UnknownEmail,

    /// <summary>The user exists in the tenant but is deactivated. Nothing sent.</summary>
    InactiveUser,

    /// <summary>The Users row exists but the tenant's Firebase project has no account for it. Nothing sent.</summary>
    FirebaseUserMissing,

    /// <summary>Configuration, the user lookup or Firebase failed before a link existed. Error logged.</summary>
    LinkFailed,

    /// <summary>A link was generated but SendGrid did not accept the email. Error logged.</summary>
    SendFailed
}
