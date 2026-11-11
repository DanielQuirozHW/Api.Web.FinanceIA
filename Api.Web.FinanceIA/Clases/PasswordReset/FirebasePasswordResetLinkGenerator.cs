// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Models;
using FirebaseAdmin.Auth;
using FirebaseSdkAuthException = FirebaseAdmin.Auth.FirebaseAuthException;

namespace Api.Web.FinanceIA.Clases.PasswordReset;

/// <summary>
/// Firebase Admin SDK implementation of <see cref="IPasswordResetLinkGenerator"/>.
/// </summary>
/// <remarks>
/// Resolves <see cref="FirebaseAuth"/> through <see cref="ITenantFirebaseRegistry"/>, keyed by
/// TenantId — never <c>FirebaseAuth.DefaultInstance</c> and never <c>FirebaseApp.GetInstance</c>
/// by project id (see <see cref="FirebaseCustomTokenIssuer"/>). The continue URL must be on a domain
/// authorised in that tenant's Firebase project, otherwise the SDK throws
/// <c>UnauthorizedContinueUrl</c> and this surfaces as <see cref="FirebaseServiceException"/>.
/// </remarks>
public sealed class FirebasePasswordResetLinkGenerator(
    ITenantFirebaseRegistry firebaseRegistry,
    ILogger<FirebasePasswordResetLinkGenerator> logger) : IPasswordResetLinkGenerator
{
    private readonly ITenantFirebaseRegistry _firebaseRegistry = firebaseRegistry;
    private readonly ILogger<FirebasePasswordResetLinkGenerator> _logger = logger;

    /// <inheritdoc />
    public async Task<string?> GenerateAsync(
        string email,
        string continueUrl,
        TenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(continueUrl);
        ArgumentNullException.ThrowIfNull(tenantContext);

        var firebaseAuth = _firebaseRegistry.GetOrCreate(tenantContext);

        try
        {
            return await firebaseAuth
                .GeneratePasswordResetLinkAsync(
                    email,
                    new ActionCodeSettings { Url = continueUrl },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        // Both codes are matched for the same reason FirebaseCustomTokenIssuer matches them: the
        // SDK reports a missing account under either, and a miss is a quiet outcome, not a failure.
        catch (FirebaseSdkAuthException ex) when (
            ex.AuthErrorCode == AuthErrorCode.UserNotFound ||
            ex.AuthErrorCode == AuthErrorCode.EmailNotFound)
        {
            _logger.LogWarning(
                "Firebase has no account for a password reset in tenant {TenantId}",
                tenantContext.TenantId);

            return null;
        }
        catch (FirebaseSdkAuthException ex)
        {
            // The exception object is not logged: Firebase messages can echo the request, and the
            // request holds the address. Type and error code are enough to diagnose.
            _logger.LogError(
                "Firebase password reset link generation failed for tenant {TenantId}. Reason: {Reason}",
                tenantContext.TenantId,
                ex.AuthErrorCode?.ToString() ?? ex.ErrorCode.ToString());

            throw new FirebaseServiceException(
                $"Firebase password reset link generation failed for tenant {tenantContext.TenantId}", ex);
        }
    }
}
