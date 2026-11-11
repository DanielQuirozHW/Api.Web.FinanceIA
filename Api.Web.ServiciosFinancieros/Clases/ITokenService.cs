// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-21
// Modified: 2026-04-21

using System.Security.Claims;
using Api.Web.ServiciosFinancieros.Models;

namespace Api.Web.ServiciosFinancieros.Clases;

/// <summary>
/// Issues an internal JWT session token based on a validated Firebase token.
/// The JWT is signed with HMAC-SHA256 and includes the tenant context.
/// </summary>
public interface ITokenService
{
    /// <summary>
    /// Issues an internal JWT session token from the claims of an authenticated
    /// <see cref="ClaimsPrincipal"/>. Extracts uid, email and roles from the principal
    /// and embeds them together with the tenant context into an HMAC-SHA256 signed JWT.
    /// </summary>
    /// <param name="principal">The authenticated claims principal. Must not be null.</param>
    /// <param name="tenantContext">The resolved tenant context. Must not be null.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>A signed internal JWT string.</returns>
    Task<string> IssueAsync(
        ClaimsPrincipal principal,
        TenantContext tenantContext,
        CancellationToken cancellationToken);
}
