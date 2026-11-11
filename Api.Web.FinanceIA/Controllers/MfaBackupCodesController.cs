// Project : Api.Web.FinanceIA
// Created : 2026-09-03

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Servicios;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// Issues, validates, counts and redeems MFA backup codes — the recovery path for a user who
/// enrolled a TOTP factor in Firebase and then lost the authenticator.
/// </summary>
/// <remarks>
/// <para>
/// Firebase Identity Platform owns the TOTP factor itself but has no backup-code API, so the
/// whole mechanism lives here. Codes are FinancIA BUSINESS data: they are stored in the FinancIA
/// business database (FinanciaDbContext) beside the [dbo].[Users] row each one belongs to, and
/// constrained to it by FK_MfaBackupCodes_Users. Every action therefore resolves the Firebase uid
/// to a Users row first and keys the codes by (TenantId, UserId) read off that row — the tenant
/// comes from the user, never from X-Tenant-ID, so a code can never be filed under a tenant its
/// owner does not belong to.
/// </para>
/// <para>
/// <b>This controller deliberately spans BOTH databases</b>, and <see cref="RedeemAsync"/> spans
/// them within a single request: the tenant's Firebase configuration (including the WebApiKey the
/// password replay needs) is auth/config data in the shared "Default" database, while the user
/// and the codes are business data in "FinanciaDefault". That is not an inconsistency to tidy
/// away — see the numbered comments in <see cref="RedeemAsync"/> for the order and why it holds.
/// </para>
/// <para>
/// <b>Three of the four actions are authenticated; <see cref="RedeemAsync"/> is not.</b> For
/// generate, validate and remaining the uid is ALWAYS resolved from the authenticated principal,
/// following the uid/sub/nameid order used across this API — never from a request body or query
/// string, since accepting it from the caller would let any authenticated user mint or spend
/// another user's codes.
/// </para>
/// <para>
/// <see cref="RedeemAsync"/> is the exception, and has to be: it serves a user mid-sign-in, whom
/// Firebase has deliberately left without a token. It is <c>[AllowAnonymous]</c> and re-proves
/// the caller's password against Identity Platform itself, then derives the uid from that
/// verified email. So the rule still holds where it counts — the uid is never taken from the
/// body — it is just established differently. See the remarks on that action.
/// </para>
/// <para>
/// Both authentication schemes are accepted, as a comma-separated list rather than a per-action
/// override (a second <c>[Authorize]</c> on an action is ANDed with the controller's, not
/// substituted for it). The list authenticates each scheme in turn and takes the first that
/// succeeds, which covers the Internal JWT — whether it arrives as a bearer token or in the
/// <c>bff_session</c> cookie — and a raw Firebase ID token from a user who has passed the first
/// factor but not yet exchanged it. The <c>SmartJwt</c> policy scheme is deliberately NOT used
/// here: it selects a scheme by reading the Authorization header, so a cookie-borne Internal JWT
/// would be routed to the Firebase handler and rejected. Both schemes prove the same identity
/// and emit the same <c>uid</c> claim, so neither is the weaker door.
/// </para>
/// <para>
/// All four actions are rate limited by <c>RateLimitingMiddleware</c> through explicit branches
/// in <c>RateLimitService</c> — see the limits documented on each action. Validate and redeem are
/// the brute-force targets and carry a per-user AND a per-IP budget. Redeem's per-IP budget is
/// not a backstop but its only real gate, since an anonymous caller has no uid for the per-user
/// branch to key on.
/// </para>
/// </remarks>
[ApiController]
[Route("api/mfa/backup-codes")]
[Route("api/bff/mfa/backup-codes")]
[Authorize(AuthenticationSchemes = "Internal," + FirebaseAuthenticationHandler.SchemeName)]
public sealed class MfaBackupCodesController : ControllerBase
{
    /// <summary>
    /// Minimum acceptable reCAPTCHA V3 score, matching the threshold the onboarding endpoint uses.
    /// </summary>
    private const double RecaptchaScoreThreshold = 0.5;

    private readonly IMfaBackupCodeService              _backupCodeService;
    private readonly IUserRepository                    _userRepository;
    private readonly ITenantContextAccessor             _tenantContextAccessor;
    private readonly IRecaptchaVerifier                 _recaptchaVerifier;
    private readonly IIdentityPlatformSignInService     _signInService;
    private readonly IFirebaseCustomTokenIssuer         _customTokenIssuer;
    private readonly ILogger<MfaBackupCodesController>  _logger;

    public MfaBackupCodesController(
        IMfaBackupCodeService             backupCodeService,
        IUserRepository                   userRepository,
        ITenantContextAccessor            tenantContextAccessor,
        IRecaptchaVerifier                recaptchaVerifier,
        IIdentityPlatformSignInService    signInService,
        IFirebaseCustomTokenIssuer        customTokenIssuer,
        ILogger<MfaBackupCodesController> logger)
    {
        _backupCodeService     = backupCodeService;
        _userRepository        = userRepository;
        _tenantContextAccessor = tenantContextAccessor;
        _recaptchaVerifier     = recaptchaVerifier;
        _signInService         = signInService;
        _customTokenIssuer     = customTokenIssuer;
        _logger                = logger;
    }

    // ── POST /api/mfa/backup-codes/generate ────────────────────────────────────

    /// <summary>
    /// Issues a fresh set of backup codes for the current user, replacing any unspent set.
    /// Called right after the user enrols their TOTP factor.
    /// </summary>
    /// <response code="200">The new codes, returned once and never again.</response>
    /// <response code="400">The authenticated principal carries no usable uid claim.</response>
    /// <response code="401">No valid session, or no FinancIA user for this identity.</response>
    /// <response code="429">Rate limit exceeded (5/min per user).</response>
    /// <remarks>
    /// The response body is the only time the plaintext exists. It is stored as a salted PBKDF2
    /// hash and is unrecoverable afterwards, so the client must present it to the user
    /// immediately; losing it means generating a new set. The response is marked
    /// <c>no-store</c> to keep it out of browser and proxy caches.
    /// </remarks>
    [HttpPost("generate")]
    [ProducesResponseType(typeof(MfaBackupCodesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GenerateAsync(CancellationToken cancellationToken)
    {
        var (tenantId, userId, error) = await ResolveCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (error is not null) return error;

        var codes = await _backupCodeService
            .GenerateAsync(
                tenantId,
                userId,
                MfaBackupCodeService.DefaultCodeCount,
                cancellationToken)
            .ConfigureAwait(false);

        // Secret material in a response body — never cached, same rule the token responses follow.
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma       = "no-cache";

        return Ok(new MfaBackupCodesResponse(codes, codes.Count));
    }

    // ── POST /api/mfa/backup-codes/validate ────────────────────────────────────

    /// <summary>
    /// Verifies a backup code for the current user and spends it on a match. Single-use: the
    /// same code never validates twice.
    /// </summary>
    /// <param name="request">The submitted code. The uid is NOT taken from the body.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <response code="200">Always, for both outcomes: <c>{ "valid": true }</c> or <c>{ "valid": false }</c>.</response>
    /// <response code="400">The authenticated principal carries no usable uid claim.</response>
    /// <response code="401">No valid session, or no FinancIA user for this identity.</response>
    /// <response code="429">Rate limit exceeded (5/min per user, 20/min per IP).</response>
    /// <remarks>
    /// <para>
    /// A wrong code answers 200 with <c>valid: false</c>, not 4xx: it is an ordinary outcome of
    /// the recovery flow, and a status code would spread the result into proxy and access logs.
    /// </para>
    /// <para>
    /// This action serves a user who ALREADY holds a session — it reads the uid from the
    /// principal, so a user stuck at <c>auth/multi-factor-auth-required</c> cannot reach it.
    /// That case is what <see cref="RedeemAsync"/> below is for.
    /// </para>
    /// </remarks>
    [HttpPost("validate")]
    [RequestSizeLimit(4 * 1024)]
    [ProducesResponseType(typeof(ValidateBackupCodeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ValidateAsync(
        [FromBody] ValidateBackupCodeRequest request,
        CancellationToken cancellationToken)
    {
        var (tenantId, userId, error) = await ResolveCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (error is not null) return error;

        // A missing code is answered like a wrong one — same shape, same status. Telling the
        // caller "you forgot the field" and "that code is wrong" apart buys an attacker nothing
        // here and costs a distinguishable response.
        var submitted = request?.Code ?? string.Empty;

        var isValid = await _backupCodeService
            .ValidateAsync(
                tenantId,
                userId,
                submitted,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken)
            .ConfigureAwait(false);

        Response.Headers.CacheControl = "no-store";

        return Ok(new ValidateBackupCodeResponse(isValid));
    }

    // ── POST /api/mfa/backup-codes/redeem ──────────────────────────────────────

    /// <summary>
    /// Completes a sign-in for a user who has passed their password but lost their
    /// authenticator: verifies email + password + backup code and, on success, returns a
    /// Firebase custom token the client redeems to finish signing in.
    /// </summary>
    /// <param name="request">Email, password, backup code and reCAPTCHA token.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <response code="200">All checks passed. Body carries the custom token.</response>
    /// <response code="400">A required field is missing.</response>
    /// <response code="401">Credentials or backup code rejected — deliberately indistinguishable.</response>
    /// <response code="422">reCAPTCHA verification failed.</response>
    /// <response code="429">Rate limit exceeded (10/min per IP).</response>
    /// <response code="502">Identity Platform or Firebase was unreachable.</response>
    /// <response code="503">This tenant has no Firebase Web API key configured.</response>
    /// <remarks>
    /// <para>
    /// <b>This is the one endpoint here that cannot require an authenticated principal</b>, and
    /// the reason is a hard Firebase constraint rather than a shortcut. When a TOTP-enrolled user
    /// signs in, Identity Platform returns an opaque <c>mfaPendingCredential</c> and NO ID token
    /// — the token is only issued by <c>mfaSignIn:finalize</c>, which requires the very TOTP code
    /// the user has lost. That pending credential cannot be checked server-side either: the Admin
    /// SDK has no multi-factor surface at all, and the REST API only ever consumes the credential,
    /// never validates it. So there is nothing the client can hand us that we could verify.
    /// </para>
    /// <para>
    /// The body therefore IS the authentication, and the BFF re-proves the first factor itself:
    /// <c>IIdentityPlatformSignInService</c> replays the password against the tenant's Firebase
    /// project, and an <c>mfaPendingCredential</c> coming back is Google's own attestation that
    /// the password is correct AND a second factor is enrolled. Only then is the backup code
    /// checked. The uid is never read from the body — it is resolved from the verified email.
    /// </para>
    /// <para>
    /// <b>Order is load-bearing.</b> reCAPTCHA, then the password, then the backup code, then the
    /// token. The password must clear before the code is touched, or anyone could burn a
    /// stranger's codes — or probe them — knowing only an email address. And the token is minted
    /// last because it is the whole prize: a custom token is redeemable for a full session, so
    /// everything that gates this recovery path has to have passed before one exists.
    /// </para>
    /// <para>
    /// Every rejection answers 401 with one message. Separating "wrong password" from "unknown
    /// account" from "wrong code" would turn this into an account-enumeration oracle and a
    /// password oracle, on an endpoint that is anonymous by construction. 502 and 503 are
    /// distinct because they are OUR failures, not the caller's, and leak nothing about them.
    /// </para>
    /// </remarks>
    [HttpPost("redeem")]
    [AllowAnonymous]
    [RequestSizeLimit(4 * 1024)]
    [ProducesResponseType(typeof(RedeemBackupCodeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RedeemAsync(
        [FromBody] RedeemBackupCodeRequest request,
        CancellationToken cancellationToken)
    {
        // Secret material both ways in and out — keep the whole exchange out of caches.
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma       = "no-cache";

        if (request is null
            || string.IsNullOrWhiteSpace(request.Email)
            || string.IsNullOrWhiteSpace(request.Password)
            || string.IsNullOrWhiteSpace(request.Code)
            || string.IsNullOrWhiteSpace(request.RecaptchaToken))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title     : "Bad Request",
                detail    : "Email, contraseña, código de recuperación y token de reCAPTCHA son obligatorios.");
        }

        // Resolved by TenantResolutionMiddleware from X-Tenant-ID. That middleware runs BEFORE
        // UseAuthentication() and is auth-unaware, so an anonymous request still arrives with a
        // full TenantContext — which is what lets this endpoint reach the tenant's Firebase
        // project without a principal.
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available for MFA backup-code redemption");

            return Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title     : "Internal Server Error",
                detail    : "Tenant context not resolved.");
        }

        if (string.IsNullOrWhiteSpace(tenantContext.FirebaseWebApiKey))
        {
            _logger.LogError(
                "MFA backup-code redemption unavailable for tenant {TenantId}: no WebApiKey configured",
                tenantContext.TenantId);

            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title     : "Service Unavailable",
                detail    : "El inicio de sesión con código de recuperación no está disponible para este tenant.");
        }

        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();

        // ── The two-database order below is deliberate; do not "simplify" it ──────────
        // a. "Default" (auth/config DB) → TenantFirebaseConfig.WebApiKey, already resolved into
        //    the TenantContext above by TenantResolutionMiddleware.
        // b. Identity Platform → replay the password (step 2 below).
        // c. "FinanciaDefault" (business DB) → Users: FirebaseUid → (UserId, TenantId) (step 3b).
        // d. "FinanciaDefault" → validate and spend the backup code, keyed by that UserId (step 4).
        // e. Firebase → mint the custom token (step 5).
        // The tenant's Firebase configuration is auth/config data and lives in one database; the
        // user and their backup codes are business data and live in the other. One request
        // legitimately touches both, in this order, and the ordering is what keeps the endpoint
        // safe: reCAPTCHA → password → user/code → token.
        // ─────────────────────────────────────────────────────────────────────────────

        // 1. reCAPTCHA, before anything that costs a Firebase call or touches a stored code.
        var recaptchaResult = await _recaptchaVerifier
            .VerifyAsync(request.RecaptchaToken, tenantContext, remoteIp, cancellationToken)
            .ConfigureAwait(false);

        if (!recaptchaResult.Success || recaptchaResult.Score < RecaptchaScoreThreshold)
        {
            _logger.LogWarning(
                "reCAPTCHA verification failed for MFA backup-code redemption. " +
                "TenantId: {TenantId} | Success: {Success} | Score: {Score}",
                tenantContext.TenantId,
                recaptchaResult.Success,
                recaptchaResult.Score);

            var problemDetails = new ProblemDetails
            {
                Status   = StatusCodes.Status422UnprocessableEntity,
                Title    = "Unprocessable Entity",
                Detail   = "Falló la verificación de reCAPTCHA.",
                Type     = "https://serviciosfinancieros.api/errors/recaptcha_failed",
                Instance = HttpContext.Request.Path
            };
            problemDetails.Extensions["errorCode"] = "recaptcha_failed";

            return StatusCode(StatusCodes.Status422UnprocessableEntity, problemDetails);
        }

        // 2. The first factor. This is the step that makes the endpoint safe to expose anonymously.
        var firstFactor = await _signInService
            .VerifyPasswordAsync(request.Email, request.Password, tenantContext, cancellationToken)
            .ConfigureAwait(false);

        if (firstFactor == FirstFactorOutcome.LookupFailed)
        {
            // Ours, not theirs: never report an outage as a credential rejection.
            return Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title     : "Bad Gateway",
                detail    : "No se pudo verificar la identidad en este momento. Intentá de nuevo.");
        }

        if (firstFactor != FirstFactorOutcome.MfaRequired)
        {
            // PasswordRejected, or NoMfaEnrolled — a correct password on an account with no
            // second factor, which has no 2FA to recover from and must not get a session here.
            _logger.LogWarning(
                "MFA backup-code redemption rejected at the first factor. " +
                "TenantId: {TenantId} | Outcome: {Outcome}",
                tenantContext.TenantId,
                firstFactor);

            return InvalidRedemption();
        }

        // 3. The uid, from the verified email — never from the body.
        var firebaseUid = await _customTokenIssuer
            .GetUidByEmailAsync(request.Email, tenantContext, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(firebaseUid))
        {
            // Identity Platform accepted the password but the Admin SDK cannot find the user:
            // odd enough to log, still answered like any other rejection.
            _logger.LogWarning(
                "MFA backup-code redemption found no Firebase user after a successful first factor. " +
                "TenantId: {TenantId}",
                tenantContext.TenantId);

            return InvalidRedemption();
        }

        // 3b. The FinancIA user, in the BUSINESS database. This is where the request crosses
        //     from "Default" into "FinanciaDefault": the codes are keyed by UserId through
        //     FK_MfaBackupCodes_Users, so the uid has to become a Users row before any code can
        //     be looked at. A Firebase account with no FinancIA user has no codes and gets the
        //     same generic rejection as every other failure — never a distinguishable one.
        var user = await _userRepository
            .GetByFirebaseUidAsync(firebaseUid, cancellationToken)
            .ConfigureAwait(false);

        if (user is null)
        {
            _logger.LogWarning(
                "MFA backup-code redemption found no FinancIA user for a verified Firebase uid. " +
                "TenantId: {TenantId}",
                tenantContext.TenantId);

            return InvalidRedemption();
        }

        // 4. The backup code, in the business database, keyed by (TenantId, UserId) taken from
        //    the user row — not from the X-Tenant-ID header. Spending is atomic: the UsedAt
        //    concurrency token means two concurrent submissions of the same code cannot both
        //    succeed.
        var isCodeValid = await _backupCodeService
            .ValidateAsync(
                user.TenantId,
                user.UserId,
                request.Code,
                remoteIp,
                cancellationToken)
            .ConfigureAwait(false);

        if (!isCodeValid)
        {
            _logger.LogWarning(
                "MFA backup-code redemption rejected: invalid or already-spent code. " +
                "TenantId: {TenantId} | UserId: {UserId}",
                tenantContext.TenantId,
                user.UserId);

            return InvalidRedemption();
        }

        // 5. Both factors are in. Mint the token.
        //    Spend-then-mint, not the reverse: it keeps "a code is spent exactly once and never
        //    yields a token twice" true even under a mid-flight failure. If minting throws here
        //    the user has lost one code of ten and can retry with the next — the safer side to
        //    fail on than a code that survives having produced a session.
        var customToken = await _customTokenIssuer
            .CreateCustomTokenAsync(firebaseUid, tenantContext, cancellationToken)
            .ConfigureAwait(false);

        var remaining = await _backupCodeService
            .RemainingCountAsync(user.TenantId, user.UserId, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "MFA backup-code redemption succeeded. " +
            "TenantId: {TenantId} | UserId: {UserId} | Remaining: {Remaining}",
            tenantContext.TenantId,
            user.UserId,
            remaining);

        return Ok(new RedeemBackupCodeResponse(customToken, remaining));
    }

    // ── GET /api/mfa/backup-codes/remaining ────────────────────────────────────

    /// <summary>
    /// Returns how many unspent backup codes the current user still holds, for a
    /// "you have N codes left" indicator.
    /// </summary>
    /// <response code="200">The remaining count.</response>
    /// <response code="400">The authenticated principal carries no usable uid claim.</response>
    /// <response code="401">No valid session, or no FinancIA user for this identity.</response>
    /// <response code="429">Rate limit exceeded (30/min per user).</response>
    [HttpGet("remaining")]
    [ProducesResponseType(typeof(RemainingBackupCodesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RemainingAsync(CancellationToken cancellationToken)
    {
        var (tenantId, userId, error) = await ResolveCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (error is not null) return error;

        var remaining = await _backupCodeService
            .RemainingCountAsync(tenantId, userId, cancellationToken)
            .ConfigureAwait(false);

        return Ok(new RemainingBackupCodesResponse(remaining));
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the Firebase uid from the authenticated principal — in the uid/sub/nameid order used
    /// across this API, which both authentication schemes emit — and resolves it to the FinancIA
    /// user the codes hang off.
    /// </summary>
    /// <returns>
    /// The owning user's TenantId and UserId with a null error, or empty values and the
    /// ProblemDetails result to return.
    /// </returns>
    /// <remarks>
    /// The tenant is taken from the RESOLVED USER ROW, not from the X-Tenant-ID header, so a code
    /// is always filed under the tenant that owns its user. Following
    /// <c>NotificationsController.ResolveCurrentUserAsync</c>, a principal with no matching
    /// [dbo].[Users] row is answered 401: it holds a Firebase identity but no FinancIA account,
    /// so there is nothing here to issue codes to.
    /// </remarks>
    private async Task<(string TenantId, Guid UserId, IActionResult? Error)> ResolveCurrentUserAsync(
        CancellationToken cancellationToken)
    {
        var firebaseUid = User.FindFirst("uid")?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrWhiteSpace(firebaseUid))
        {
            _logger.LogWarning(
                "MFA backup code request without a usable uid claim. Path: {Path}",
                HttpContext.Request.Path);

            return (string.Empty, Guid.Empty, Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title     : "Bad Request",
                detail    : "No se pudo determinar el usuario autenticado."));
        }

        var user = await _userRepository
            .GetByFirebaseUidAsync(firebaseUid, cancellationToken)
            .ConfigureAwait(false);

        if (user is null)
        {
            _logger.LogWarning(
                "MFA backup code request from a principal with no FinancIA user record. " +
                "Path: {Path}",
                HttpContext.Request.Path);

            return (string.Empty, Guid.Empty, Unauthorized());
        }

        return (user.TenantId, user.UserId, null);
    }

    /// <summary>
    /// The single rejection used by every failed redemption.
    /// </summary>
    /// <remarks>
    /// One status, one message, one shape for a wrong password, an unknown account, an account
    /// with no second factor, and a wrong or already-spent backup code. Any difference between
    /// them — status, wording, even a distinguishable response time — would make an anonymous
    /// endpoint into an account-enumeration and password oracle. The specifics go to the log,
    /// where they are useful and not reachable by the caller.
    /// </remarks>
    private IActionResult InvalidRedemption()
        => Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title     : "Unauthorized",
            detail    : "Credenciales o código de recuperación inválidos.");
}
