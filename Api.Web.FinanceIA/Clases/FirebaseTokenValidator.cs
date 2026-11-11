// Project : Api.Web.FinanceIA
// Created : 2026-04-20
// Modified: 2026-04-20

using FirebaseAdmin.Auth;
using Microsoft.Extensions.Logging;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Models;
using FirebaseSdkAuthException = FirebaseAdmin.Auth.FirebaseAuthException;
using AppFirebaseAuthException = Api.Web.FinanceIA.Clases.Exceptions.FirebaseAuthException;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Validates Firebase ID tokens against the tenant-specific <see cref="FirebaseAuth"/> instance
/// retrieved from <see cref="ITenantFirebaseRegistry"/>. Applies lazy initialization — the
/// <see cref="FirebaseAdmin.FirebaseApp"/> is created on first request per tenant and cached.
/// Never uses <see cref="FirebaseAuth.DefaultInstance"/>.
/// </summary>
public sealed class FirebaseTokenValidator(
    ITenantFirebaseRegistry firebaseRegistry,
    ILogger<FirebaseTokenValidator> logger) : IFirebaseTokenValidator
{
    private readonly ITenantFirebaseRegistry _firebaseRegistry = firebaseRegistry;
    private readonly ILogger<FirebaseTokenValidator> _logger = logger;

    /// <summary>
    /// Validates the Firebase ID token against the tenant-specific Firebase project.
    /// Retrieves the correct <see cref="FirebaseAuth"/> instance from the registry
    /// using the <paramref name="tenantContext"/>, then calls
    /// <see cref="FirebaseAuth.VerifyIdTokenAsync(string, CancellationToken)"/>.
    /// </summary>
    /// <param name="idToken">The Firebase ID token string. Must not be null or whitespace.</param>
    /// <param name="tenantContext">The resolved tenant context. Must not be null.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>The decoded and verified <see cref="FirebaseToken"/>.</returns>
    /// <exception cref="AppFirebaseAuthException">
    /// Thrown when the token is invalid, expired, or from the wrong Firebase project.
    /// </exception>
    /// <exception cref="FirebaseServiceException">
    /// Thrown when the Firebase Admin SDK fails for a non-authentication reason.
    /// </exception>
    public async Task<FirebaseToken> ValidateAsync(
        string idToken,
        TenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idToken);
        ArgumentNullException.ThrowIfNull(tenantContext);

        var firebaseAuth = _firebaseRegistry.GetOrCreate(tenantContext);

        try
        {
            _logger.LogDebug(
                "Validating Firebase token for tenant {TenantId}",
                tenantContext.TenantId);

            var firebaseToken = await firebaseAuth
                .VerifyIdTokenAsync(idToken, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Firebase token validated successfully for tenant {TenantId} uid {Uid}",
                tenantContext.TenantId,
                firebaseToken.Uid);

            return firebaseToken;
        }
        catch (FirebaseSdkAuthException ex)
        {
            _logger.LogWarning(
                "Firebase token validation failed for tenant {TenantId}. Reason: {Reason}",
                tenantContext.TenantId,
                ex.AuthErrorCode.ToString());

            throw new AppFirebaseAuthException(
                $"Token validation failed: {ex.AuthErrorCode}", ex);
        }
        catch (Exception ex) when (ex is not AppFirebaseAuthException)
        {
            _logger.LogError(ex,
                "Firebase service error for tenant {TenantId}",
                tenantContext.TenantId);

            throw new FirebaseServiceException(
                $"Firebase service error for tenant {tenantContext.TenantId}", ex);
        }
    }
}
