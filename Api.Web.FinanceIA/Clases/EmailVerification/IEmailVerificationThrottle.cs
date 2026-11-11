// Project : Api.Web.FinanceIA
// Created : 2026-10-21

using Api.Web.FinanceIA.Clases.PasswordReset;

namespace Api.Web.FinanceIA.Clases.EmailVerification;

/// <summary>
/// Per-(tenant, user) budget for POST api/bff/auth/email-verification: by default 3 requests per 15
/// minutes, because each accepted request sends an email. It is the endpoint's ONLY limit:
/// RateLimitService has no branch for this path, and an unrecognised path is checked against nothing.
/// </summary>
public interface IEmailVerificationThrottle
{
    /// <summary>
    /// Counts one request for <paramref name="userId"/> in <paramref name="tenantId"/> and says whether
    /// it fits the window. Called for EVERY request, before the Firebase lookup, so the budget also
    /// caps how often one session can make the BFF call Firebase.
    /// </summary>
    PasswordResetThrottleDecision Register(string tenantId, Guid userId);
}
