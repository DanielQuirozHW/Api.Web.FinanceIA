// Project : Api.Web.FinanceIA
// Created : 2026-04-21
// Modified: 2026-04-21

using Api.Web.FinanceIA.Models;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Issues an internal JWT session token based on a validated Firebase token.
/// The JWT is signed with HMAC-SHA256 and includes the tenant context.
/// </summary>
public interface ITokenService
{
    /// <summary>
    /// Issues an internal JWT session token for the given Firebase UID.
    /// Embeds tenant context, user identity, role and access scope
    /// into an HMAC-SHA256 signed JWT.
    /// </summary>
    /// <param name="firebaseUid">The decrypted Firebase UID. Must not be null or empty.</param>
    /// <param name="tenantContext">The resolved tenant context. Must not be null.</param>
    /// <param name="userId">The internal SQL user ID.</param>
    /// <param name="role">The user role.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>A signed internal JWT string.</returns>
    Task<string> IssueAsync(
        string firebaseUid,
        TenantContext tenantContext,
        Guid userId,
        string role,
        CancellationToken cancellationToken);
}
