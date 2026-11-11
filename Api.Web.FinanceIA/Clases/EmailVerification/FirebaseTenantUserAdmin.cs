// Project : Api.Web.FinanceIA
// Created : 2026-10-21

using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Models;
using FirebaseAdmin.Auth;
using FirebaseSdkAuthException = FirebaseAdmin.Auth.FirebaseAuthException;

namespace Api.Web.FinanceIA.Clases.EmailVerification;

/// <summary>
/// Firebase Admin SDK implementation of <see cref="ITenantFirebaseUserAdmin"/>.
/// </summary>
/// <remarks>
/// Resolves <see cref="FirebaseAuth"/> through <see cref="ITenantFirebaseRegistry"/>, keyed by
/// TenantId — never <c>FirebaseAuth.DefaultInstance</c>, the same way
/// <see cref="PasswordReset.FirebasePasswordResetLinkGenerator"/> does. The continue URL must be on a
/// domain authorised in that tenant's Firebase project, otherwise the SDK throws
/// <c>UnauthorizedContinueUrl</c> and this surfaces as <see cref="FirebaseServiceException"/>.
/// </remarks>
public sealed class FirebaseTenantUserAdmin(
    ITenantFirebaseRegistry firebaseRegistry,
    ILogger<FirebaseTenantUserAdmin> logger) : ITenantFirebaseUserAdmin
{
    private readonly ITenantFirebaseRegistry _firebaseRegistry = firebaseRegistry;
    private readonly ILogger<FirebaseTenantUserAdmin> _logger = logger;

    /// <inheritdoc />
    public async Task<bool?> GetEmailVerifiedAsync(
        string firebaseUid,
        TenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firebaseUid);
        ArgumentNullException.ThrowIfNull(tenantContext);

        var firebaseAuth = _firebaseRegistry.GetOrCreate(tenantContext);

        try
        {
            var record = await firebaseAuth
                .GetUserAsync(firebaseUid, cancellationToken)
                .ConfigureAwait(false);

            return record.EmailVerified;
        }
        catch (FirebaseSdkAuthException ex) when (IsMissingAccount(ex))
        {
            _logger.LogWarning(
                "Firebase has no account for an email verification lookup in tenant {TenantId}",
                tenantContext.TenantId);

            return null;
        }
        catch (FirebaseSdkAuthException ex)
        {
            throw Failure("user lookup", tenantContext, ex);
        }
    }

    /// <inheritdoc />
    public async Task MarkEmailVerifiedAsync(
        string firebaseUid,
        TenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firebaseUid);
        ArgumentNullException.ThrowIfNull(tenantContext);

        var firebaseAuth = _firebaseRegistry.GetOrCreate(tenantContext);

        try
        {
            await firebaseAuth
                .UpdateUserAsync(
                    new UserRecordArgs { Uid = firebaseUid, EmailVerified = true },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FirebaseSdkAuthException ex)
        {
            throw Failure("email verified update", tenantContext, ex);
        }
    }

    /// <inheritdoc />
    public async Task<string?> GenerateEmailVerificationLinkAsync(
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
                .GenerateEmailVerificationLinkAsync(
                    email,
                    new ActionCodeSettings { Url = continueUrl },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FirebaseSdkAuthException ex) when (IsMissingAccount(ex))
        {
            _logger.LogWarning(
                "Firebase has no account for an email verification link in tenant {TenantId}",
                tenantContext.TenantId);

            return null;
        }
        catch (FirebaseSdkAuthException ex)
        {
            throw Failure("email verification link generation", tenantContext, ex);
        }
    }

    // Both codes are matched for the same reason FirebaseCustomTokenIssuer matches them: the SDK
    // reports a missing account under either.
    private static bool IsMissingAccount(FirebaseSdkAuthException ex) =>
        ex.AuthErrorCode == AuthErrorCode.UserNotFound || ex.AuthErrorCode == AuthErrorCode.EmailNotFound;

    private FirebaseServiceException Failure(string operation, TenantContext tenantContext, FirebaseSdkAuthException ex)
    {
        // The exception object is not logged: Firebase messages can echo the request, and the
        // request holds the address. Operation and error code are enough to diagnose.
        _logger.LogError(
            "Firebase {Operation} failed for tenant {TenantId}. Reason: {Reason}",
            operation,
            tenantContext.TenantId,
            ex.AuthErrorCode?.ToString() ?? ex.ErrorCode.ToString());

        return new FirebaseServiceException(
            $"Firebase {operation} failed for tenant {tenantContext.TenantId}", ex);
    }
}
