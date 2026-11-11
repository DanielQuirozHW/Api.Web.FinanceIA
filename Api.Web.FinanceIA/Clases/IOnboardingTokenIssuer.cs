// Project : Api.Web.FinanceIA
// Created : 2026-04-21
// Modified: 2026-04-21

using Api.Web.FinanceIA.Models;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Issues a short-lived anonymous onboarding JWT for unauthenticated onboarding flows.
/// The token contains minimal claims and is scoped strictly to onboarding operations.
/// </summary>
public interface IOnboardingTokenIssuer
{
    /// <summary>
    /// Issues a short-lived onboarding JWT for the given tenant context.
    /// The token scope is limited to <c>onboarding_anon</c> and contains no user identity claims.
    /// </summary>
    /// <param name="tenantContext">The resolved tenant context. Must not be null.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>A signed onboarding JWT string.</returns>
    Task<string> IssueAsync(
        TenantContext tenantContext,
        CancellationToken cancellationToken);
}
