// Project : Api.Web.FinanceIA
// Created : 2026-09-03

using Api.Web.FinanceIA.Models;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// How a first-factor (email + password) check against Identity Platform came out.
/// </summary>
/// <remarks>
/// A sentinel enum rather than a nullable return, following the convention every other
/// vendor integration in this project uses: collapsing "the vendor said no" into "the call
/// failed" silently converts an outage into a confident wrong answer. Here the stakes are
/// higher than usual — <see cref="LookupFailed"/> answered as <see cref="PasswordRejected"/>
/// would tell a user with the right password that their credentials are wrong.
/// </remarks>
public enum FirstFactorOutcome
{
    /// <summary>
    /// The password is correct AND the account has a second factor enrolled: Identity Platform
    /// answered with an <c>mfaPendingCredential</c> instead of an ID token. This is the only
    /// success for the backup-code login flow — it is Google's own attestation that the first
    /// factor passed, which is exactly what a user stuck at
    /// <c>auth/multi-factor-auth-required</c> cannot otherwise prove to us.
    /// </summary>
    MfaRequired,

    /// <summary>
    /// Identity Platform rejected the credentials (wrong password, or no such account —
    /// modern Identity Platform deliberately conflates the two).
    /// </summary>
    PasswordRejected,

    /// <summary>
    /// The password is correct but the account has NO second factor enrolled: the response
    /// carried a normal ID token. Not a valid use of this endpoint — there is no 2FA to
    /// recover from — and it must not mint a session, or the backup-code path would become a
    /// way to sign in that skips whatever the account's real first-factor flow is.
    /// </summary>
    NoMfaEnrolled,

    /// <summary>
    /// The call itself failed: network error, timeout, missing API key, or an unrecognised
    /// response. Says nothing about the credentials.
    /// </summary>
    LookupFailed,
}

/// <summary>
/// Verifies a user's first factor (email + password) directly against Identity Platform's
/// REST API, on behalf of a caller who cannot prove it any other way.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of a hard limitation in Firebase, not by preference. When a user with
/// TOTP enrolled signs in, Identity Platform returns an opaque <c>mfaPendingCredential</c> and
/// <b>no ID token</b> — the ID token is only issued by <c>mfaSignIn:finalize</c>, which demands
/// the very TOTP code a user with a lost authenticator does not have. The pending credential
/// cannot be verified server-side either: the Admin SDK has no multi-factor surface at all, and
/// the REST API only ever consumes the credential, never validates it. So a backend cannot
/// authenticate an MFA-pending user from anything the client can hand it.
/// </para>
/// <para>
/// Re-running the password check here is therefore the only way to obtain a first-factor proof
/// that is produced by Google rather than asserted by the caller.
/// </para>
/// </remarks>
public interface IIdentityPlatformSignInService
{
    /// <summary>
    /// Attempts a password sign-in against the tenant's Firebase project and reports how it went.
    /// </summary>
    /// <param name="email">The submitted email address.</param>
    /// <param name="password">The submitted password. Never logged, never persisted.</param>
    /// <param name="tenantContext">
    /// The resolved tenant, supplying the project's Web API key. Passed as a parameter rather
    /// than injected via ITenantContextAccessor, per this project's rules.
    /// </param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>The outcome. Never null, never throws for an expected rejection.</returns>
    Task<FirstFactorOutcome> VerifyPasswordAsync(
        string email,
        string password,
        TenantContext tenantContext,
        CancellationToken cancellationToken);
}
