// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Clases.PasswordReset;

/// <summary>
/// Per-(tenant, email) budget for POST api/bff/auth/password-reset. The rate-limit middleware runs
/// before model binding and cannot see the body, so this budget lives in the controller.
/// </summary>
public interface IPasswordResetEmailThrottle
{
    /// <summary>
    /// Counts one request for <paramref name="email"/> in <paramref name="tenantId"/> and says
    /// whether it fits the window. Call it for EVERY well-formed request, before any user lookup,
    /// so the answer cannot depend on whether the account exists.
    /// </summary>
    PasswordResetThrottleDecision Register(string tenantId, string email);
}
