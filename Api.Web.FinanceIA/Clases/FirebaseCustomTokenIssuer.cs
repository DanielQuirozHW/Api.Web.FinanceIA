// Project : Api.Web.FinanceIA
// Created : 2026-09-03

using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Models;
using FirebaseAdmin.Auth;
using FirebaseSdkAuthException = FirebaseAdmin.Auth.FirebaseAuthException;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Firebase Admin SDK implementation of <see cref="IFirebaseCustomTokenIssuer"/>.
/// </summary>
/// <remarks>
/// <para>
/// Resolves <see cref="FirebaseAuth"/> through <see cref="ITenantFirebaseRegistry"/> — never
/// <c>FirebaseApp.GetInstance(...)</c>. The registry keys its apps by <c>TenantId</c>, so
/// looking one up by <c>FirebaseProjectId</c> throws for every tenant whose slug and project id
/// differ. <c>InvitationController</c> has that bug today; do not copy it here.
/// </para>
/// <para>
/// Custom token signing is local: <c>TenantFirebaseConfig.CredentialJson</c> is a full
/// service-account key, so <c>GoogleCredential.FromJson</c> yields a ServiceAccountCredential
/// that signs with its own private key — no IAM signBlob round trip and no extra configuration.
/// </para>
/// <para>
/// This project runs one Firebase PROJECT per tenant rather than Identity Platform
/// multi-tenancy, so no <c>tenant_id</c> claim belongs in the token: isolation comes from which
/// project's private key signed it. A token minted for tenant A is simply not valid for
/// tenant B's project. (Under GCIP multi-tenancy this would instead need
/// <c>TenantManager.AuthForTenant</c> and a matching tenant id on the client.)
/// </para>
/// </remarks>
public sealed class FirebaseCustomTokenIssuer(
    ITenantFirebaseRegistry firebaseRegistry,
    ILogger<FirebaseCustomTokenIssuer> logger) : IFirebaseCustomTokenIssuer
{
    private readonly ITenantFirebaseRegistry _firebaseRegistry = firebaseRegistry;
    private readonly ILogger<FirebaseCustomTokenIssuer> _logger = logger;

    /// <inheritdoc />
    public async Task<string?> GetUidByEmailAsync(
        string email,
        TenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentNullException.ThrowIfNull(tenantContext);

        var firebaseAuth = _firebaseRegistry.GetOrCreate(tenantContext);

        try
        {
            var userRecord = await firebaseAuth
                .GetUserByEmailAsync(email, cancellationToken)
                .ConfigureAwait(false);

            return userRecord.Uid;
        }
        // Both codes are matched deliberately. UserNotFound is what this lookup documents, but
        // the SDK also carries a near-synonymous EmailNotFound, and treating a miss as a hard
        // failure here would turn "no such account" into a 502 instead of the generic rejection
        // the caller must get.
        catch (FirebaseSdkAuthException ex) when (
            ex.AuthErrorCode == AuthErrorCode.UserNotFound ||
            ex.AuthErrorCode == AuthErrorCode.EmailNotFound)
        {
            // Not an error condition: the caller turns this into the same generic rejection as a
            // wrong password, so an unknown address cannot be distinguished from a known one.
            _logger.LogWarning(
                "Firebase user lookup found no account for tenant {TenantId}",
                tenantContext.TenantId);

            return null;
        }
        catch (FirebaseSdkAuthException ex)
        {
            _logger.LogError(ex,
                "Firebase user lookup failed for tenant {TenantId}. Reason: {Reason}",
                tenantContext.TenantId,
                ex.AuthErrorCode.ToString());

            throw new FirebaseServiceException(
                $"Firebase user lookup failed for tenant {tenantContext.TenantId}", ex);
        }
    }

    /// <inheritdoc />
    public async Task<string> CreateCustomTokenAsync(
        string firebaseUid,
        TenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firebaseUid);
        ArgumentNullException.ThrowIfNull(tenantContext);

        var firebaseAuth = _firebaseRegistry.GetOrCreate(tenantContext);

        try
        {
            var customToken = await firebaseAuth
                .CreateCustomTokenAsync(firebaseUid, cancellationToken)
                .ConfigureAwait(false);

            // The uid is safe to log; the token never is — it is redeemable for a session.
            _logger.LogInformation(
                "Firebase custom token minted for tenant {TenantId} uid {Uid}",
                tenantContext.TenantId,
                firebaseUid);

            return customToken;
        }
        catch (FirebaseSdkAuthException ex)
        {
            _logger.LogError(ex,
                "Firebase custom token minting failed for tenant {TenantId} uid {Uid}. Reason: {Reason}",
                tenantContext.TenantId,
                firebaseUid,
                ex.AuthErrorCode.ToString());

            throw new FirebaseServiceException(
                $"Firebase custom token minting failed for tenant {tenantContext.TenantId}", ex);
        }
    }
}
