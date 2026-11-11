// Project : Api.Web.FinanceIA
// Created : 2026-09-03

using Api.Web.FinanceIA.Models;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Looks up a Firebase uid by email and mints a Firebase custom token for it, using the
/// tenant's own Firebase project.
/// </summary>
/// <remarks>
/// <para>
/// Both operations are wrapped behind this interface rather than called inline for one
/// practical reason: they go through <c>FirebaseAuth</c>, a concrete SDK type that cannot be
/// hand-stubbed, and this project's tests use hand-rolled stubs with no mocking library. Without
/// the seam the redeem endpoint would be untestable.
/// </para>
/// <para>
/// A custom token is the key to a session, so treat it exactly like one: never log it, never
/// persist it, and only ever mint one after every check the caller must pass has passed.
/// </para>
/// </remarks>
public interface IFirebaseCustomTokenIssuer
{
    /// <summary>
    /// Resolves the Firebase uid for an email address in the tenant's project.
    /// </summary>
    /// <param name="email">The email address to look up.</param>
    /// <param name="tenantContext">The resolved tenant, selecting the Firebase project.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>The uid, or null when no such user exists in that project.</returns>
    /// <remarks>
    /// Needed because Identity Platform's MFA-pending sign-in response carries the pending
    /// credential and the enrolled-factor hints, but no <c>localId</c> — so the uid has to be
    /// fetched separately before the backup code can be checked against it.
    /// </remarks>
    Task<string?> GetUidByEmailAsync(
        string email,
        TenantContext tenantContext,
        CancellationToken cancellationToken);

    /// <summary>
    /// Mints a Firebase custom token for the given uid.
    /// </summary>
    /// <param name="firebaseUid">The uid to mint for.</param>
    /// <param name="tenantContext">The resolved tenant, selecting the Firebase project.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>The signed custom token, for the client to redeem with signInWithCustomToken.</returns>
    Task<string> CreateCustomTokenAsync(
        string firebaseUid,
        TenantContext tenantContext,
        CancellationToken cancellationToken);
}
