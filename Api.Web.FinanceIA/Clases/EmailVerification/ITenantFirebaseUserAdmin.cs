// Project : Api.Web.FinanceIA
// Created : 2026-10-21

using Api.Web.FinanceIA.Models;

namespace Api.Web.FinanceIA.Clases.EmailVerification;

/// <summary>
/// The Firebase Admin operations behind email verification, run with the TENANT's Firebase app. A
/// seam over the sealed <c>FirebaseAuth</c>, so the invitation and verification flows can be tested
/// without Firebase.
/// </summary>
/// <remarks>
/// Every failure other than a missing account throws <see cref="Exceptions.FirebaseServiceException"/>
/// (an SDK failure, logged by its Firebase reason only) or whatever the registry throws while building
/// the tenant's app. Firebase messages can echo the request, so callers log the exception TYPE only.
/// </remarks>
public interface ITenantFirebaseUserAdmin
{
    /// <summary>
    /// Whether Firebase marks the email of <paramref name="firebaseUid"/> as verified, or null when the
    /// tenant's Firebase project has no such account.
    /// </summary>
    Task<bool?> GetEmailVerifiedAsync(
        string firebaseUid,
        TenantContext tenantContext,
        CancellationToken cancellationToken);

    /// <summary>Sets <c>EmailVerified = true</c> on <paramref name="firebaseUid"/>.</summary>
    Task MarkEmailVerifiedAsync(
        string firebaseUid,
        TenantContext tenantContext,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the email-verification action link Firebase generated for <paramref name="email"/>,
    /// with <paramref name="continueUrl"/> as its <c>ActionCodeSettings.Url</c>, or null when the
    /// tenant's Firebase project has no account for that address. The returned link is a credential:
    /// never log it.
    /// </summary>
    Task<string?> GenerateEmailVerificationLinkAsync(
        string email,
        string continueUrl,
        TenantContext tenantContext,
        CancellationToken cancellationToken);
}
