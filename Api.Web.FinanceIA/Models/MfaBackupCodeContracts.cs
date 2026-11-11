// Project : Api.Web.FinanceIA
// Created : 2026-09-03

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// Response of POST /api/mfa/backup-codes/generate.
/// The ONLY place a backup code ever appears in plaintext: it is not stored, not logged and
/// cannot be retrieved again. The client must show it to the user immediately.
/// </summary>
/// <param name="Codes">The freshly issued codes in display form ("ABCD-2345").</param>
/// <param name="Count">How many codes were issued — the length of <paramref name="Codes"/>.</param>
public sealed record MfaBackupCodesResponse(
    IReadOnlyList<string> Codes,
    int Count);

/// <summary>
/// Body of POST /api/mfa/backup-codes/validate.
/// Carries the code and NOTHING else — the uid is always read from the authenticated
/// principal, never accepted from the caller.
/// </summary>
/// <param name="Code">The submitted code. Case and the display hyphen are insignificant.</param>
public sealed record ValidateBackupCodeRequest(string? Code);

/// <summary>
/// Response of POST /api/mfa/backup-codes/validate.
/// Answers 200 in both directions: a wrong code is an expected outcome of the recovery flow,
/// not a transport-level failure, and a 4xx would leak the difference into logs and proxies.
/// </summary>
/// <param name="Valid">True when the code matched and was spent by this call.</param>
public sealed record ValidateBackupCodeResponse(bool Valid);

/// <summary>
/// Response of GET /api/mfa/backup-codes/remaining.
/// </summary>
/// <param name="Remaining">How many unspent codes the user still holds.</param>
public sealed record RemainingBackupCodesResponse(int Remaining);

/// <summary>
/// Body of POST /api/mfa/backup-codes/redeem — the 2FA recovery login.
/// </summary>
/// <remarks>
/// This is the one endpoint in the API that cannot read its user from an authenticated
/// principal, because the caller is mid-sign-in and Firebase has not issued them a token: it
/// answers <c>auth/multi-factor-auth-required</c> and withholds the ID token until a second
/// factor passes. So the credentials ARE the authentication, and the BFF re-proves the first
/// factor against Identity Platform itself rather than trusting anything the caller asserts.
/// </remarks>
/// <param name="Email">The account's email address, used as the first factor and to resolve the uid.</param>
/// <param name="Password">The account's password. Verified against Identity Platform, never stored or logged.</param>
/// <param name="Code">The backup code. Case and the display hyphen are insignificant.</param>
/// <param name="RecaptchaToken">reCAPTCHA V3 token — required, as on every anonymous endpoint here.</param>
public sealed record RedeemBackupCodeRequest(
    string? Email,
    string? Password,
    string? Code,
    string? RecaptchaToken);

/// <summary>
/// Response of POST /api/mfa/backup-codes/redeem.
/// </summary>
/// <remarks>
/// Unlike <see cref="ValidateBackupCodeResponse"/>, which answers 200 in both directions, a 200
/// here always means a token was minted — a failure must not return one, so it is a 401 instead.
/// The client redeems <paramref name="CustomToken"/> with <c>signInWithCustomToken</c> and then
/// exchanges the resulting Firebase ID token at POST /api/bff/auth/token as usual.
/// </remarks>
/// <param name="CustomToken">A Firebase custom token for the account. Short-lived; treat as a credential.</param>
/// <param name="Remaining">
/// How many unspent backup codes are left after this one was spent, so the client can warn a
/// user who is running out before they are locked out for good.
/// </param>
public sealed record RedeemBackupCodeResponse(
    string CustomToken,
    int Remaining);
