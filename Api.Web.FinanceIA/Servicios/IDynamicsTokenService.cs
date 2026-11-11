// Project : Api.Web.FinanceIA
// Created : 2026-04-21
// Modified: 2026-04-21

using Api.Web.FinanceIA.Models;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Acquires an access token for Dynamics 365 using MSAL client credentials flow.
/// Each request builds a new ConfidentialClientApplication with tenant-specific credentials.
/// MSAL handles token caching internally.
/// </summary>
public interface IDynamicsTokenService
{
    /// <summary>
    /// Acquires a Dynamics 365 access token for the given tenant using MSAL
    /// client credentials flow with the credentials stored in <paramref name="tenantContext"/>.
    /// </summary>
    /// <param name="tenantContext">The resolved tenant context providing credentials. Must not be null.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>A valid Dynamics 365 access token string.</returns>
    /// <exception cref="Api.Web.FinanceIA.Clases.Exceptions.DynamicsAuthException">
    /// Thrown when MSAL fails to acquire the token.
    /// </exception>
    Task<string> GetAccessTokenAsync(
        TenantContext tenantContext,
        CancellationToken cancellationToken);
}
