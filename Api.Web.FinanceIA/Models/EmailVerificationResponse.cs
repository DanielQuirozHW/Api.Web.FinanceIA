// Project : Api.Web.FinanceIA
// Created : 2026-10-21

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// Body of POST api/bff/auth/email-verification: 200 with <c>alreadyVerified: true</c> when Firebase
/// already marks the address verified (no email is sent), 202 with <c>alreadyVerified: false</c> when
/// the email was queued.
/// </summary>
public sealed record EmailVerificationResponse
{
    /// <summary>True when nothing was sent because the address is already verified.</summary>
    public required bool AlreadyVerified { get; init; }
}
