// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-20
// Modified: 2026-04-20

using FirebaseAdmin.Auth;
using Api.Web.ServiciosFinancieros.Models;

namespace Api.Web.ServiciosFinancieros.Clases;

/// <summary>
/// Validates a Firebase ID token against the tenant-specific FirebaseApp instance.
/// Never uses <see cref="FirebaseAuth.DefaultInstance"/>.
/// </summary>
public interface IFirebaseTokenValidator
{
    /// <summary>
    /// Validates the Firebase ID token and returns the decoded <see cref="FirebaseToken"/>
    /// if the token is authentic and has not expired.
    /// </summary>
    /// <param name="idToken">The Firebase ID token to validate. Must not be null or whitespace.</param>
    /// <param name="tenantContext">
    /// The resolved tenant context. Used to select the correct <see cref="FirebaseAuth"/> instance.
    /// Must not be null.
    /// </param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>The decoded and verified <see cref="FirebaseToken"/>.</returns>
    /// <exception cref="Exceptions.FirebaseAuthException">
    /// Thrown when the token is invalid, expired, or issued by the wrong Firebase project.
    /// </exception>
    /// <exception cref="Exceptions.FirebaseServiceException">
    /// Thrown when the Firebase Admin SDK fails for a non-authentication reason.
    /// </exception>
    Task<FirebaseToken> ValidateAsync(
        string idToken,
        TenantContext tenantContext,
        CancellationToken cancellationToken);
}
