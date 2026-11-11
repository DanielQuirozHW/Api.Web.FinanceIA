// Project : Api.Web.FinanceIA
// Created : 2026-10-21

namespace Api.Web.FinanceIA.Clases.EmailVerification;

/// <summary>
/// What happened to one email-verification request. Logged with the tenant and the UserId only —
/// never the address or the link.
/// </summary>
public enum EmailVerificationOutcome
{
    /// <summary>Link generated and SendGrid accepted the email.</summary>
    Sent,

    /// <summary>Firebase already marks the address verified. Answered 200 by the controller; nothing sent.</summary>
    AlreadyVerified,

    /// <summary>The user, the Portal URL or Firebase failed before a link existed. Nothing sent.</summary>
    LinkFailed,

    /// <summary>A link was generated but SendGrid did not accept the email. Error logged.</summary>
    SendFailed,

    /// <summary>Neither the tenant nor the global configuration names a SendGrid template. Nothing generated or sent.</summary>
    TemplateMissing
}
