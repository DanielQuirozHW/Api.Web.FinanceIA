// Project : Api.Web.FinanceIA
// Created : 2026-04-21
// Modified: 2026-10-26

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// Backend-For-Frontend authentication controller.
/// Handles Firebase token exchange for internal JWTs, session refresh,
/// logout and invitation-token pre-validation.
/// <para>
/// There is deliberately no self-registration here. A Users row is created only by
/// POST api/bff/auth/register-invited (<see cref="InvitationController"/>), which requires a
/// live invitation for the resolved tenant and takes the role from it. The former
/// auth/register (any Firebase user of the tenant's project became an Analyst) and
/// auth/complete-registration (anonymous, Firebase uid taken from the body) were removed.
/// </para>
/// </summary>
[ApiController]
[Route("api/bff")]
public sealed class BffController : ControllerBase
{
    private const string SessionCookieName = "bff_session";

    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly IFirebaseTokenValidator _firebaseTokenValidator;
    private readonly ITokenService _tokenService;
    private readonly IOnboardingTokenIssuer _onboardingTokenIssuer;
    private readonly IRecaptchaVerifier _recaptchaVerifier;
    private readonly IUserRepository _userRepository;
    private readonly IUserInvitationService _userInvitationService;
    private readonly ITenantSettingsService _tenantSettingsService;
    private readonly IWebHostEnvironment _environment;
    private readonly OnboardingOptions _onboardingOptions;
    private readonly ILogger<BffController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="BffController"/>.
    /// </summary>
    public BffController(
        ITenantContextAccessor tenantContextAccessor,
        IFirebaseTokenValidator firebaseTokenValidator,
        ITokenService tokenService,
        IOnboardingTokenIssuer onboardingTokenIssuer,
        IRecaptchaVerifier recaptchaVerifier,
        IUserRepository userRepository,
        IUserInvitationService userInvitationService,
        ITenantSettingsService tenantSettingsService,
        IWebHostEnvironment environment,
        IOptions<OnboardingOptions> onboardingOptions,
        ILogger<BffController> logger)
    {
        _tenantContextAccessor = tenantContextAccessor;
        _firebaseTokenValidator = firebaseTokenValidator;
        _tokenService = tokenService;
        _onboardingTokenIssuer = onboardingTokenIssuer;
        _recaptchaVerifier = recaptchaVerifier;
        _userRepository = userRepository;
        _userInvitationService = userInvitationService;
        _tenantSettingsService = tenantSettingsService;
        _environment = environment;
        _onboardingOptions = onboardingOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Exchanges a validated Firebase ID token for an internal JWT.
    /// The Firebase token is validated by <see cref="FirebaseAuthenticationHandler"/>
    /// before this action executes. Returns the JWT as an httpOnly cookie.
    /// </summary>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK. The signed internal JWT is set as an httpOnly cookie.</returns>
    [HttpPost("auth/token")]
    [Authorize(AuthenticationSchemes = FirebaseAuthenticationHandler.SchemeName)]
    [ProducesResponseType(200)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> ExchangeFirebaseTokenAsync(CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in BffController");
            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        var uid = User.FindFirst("uid")?.Value;
        if (uid is null)
        {
            return Unauthorized();
        }

        // Look up user to get internal UserId for JWT claims
        var user = await _userRepository.GetByFirebaseUidAsync(uid, cancellationToken)
            .ConfigureAwait(false);

        if (user is null)
        {
            // Fallback: try by email (handles encrypted FirebaseUid scenarios)
            var email = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                        ?? User.FindFirst("email")?.Value;

            if (!string.IsNullOrWhiteSpace(email))
            {
                user = await _userRepository.GetByEmailAsync(email, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (user is null)
        {
            _logger.LogWarning(
                "User not found for uid {Uid} in tenant {TenantId}",
                uid, tenantContext.TenantId);
            return Problem(
                statusCode: 404,
                title: "Not Found",
                detail: "El usuario no está registrado.");
        }

        // Use the stored FirebaseUid (may be encrypted) so JWT uid claim matches DB
        var token = await _tokenService.IssueAsync(user.FirebaseUid, tenantContext, user.UserId, user.Role, cancellationToken);

        SetSessionCookie(token);

        _logger.LogInformation(
            "Internal JWT issued for tenant {TenantId} uid {Uid}",
            tenantContext.TenantId,
            uid);

        return Ok();
    }

    /// <summary>
    /// Issues a short-lived onboarding JWT for unauthenticated users.
    /// Requires a valid reCAPTCHA V3 token with score >= <see cref="OnboardingOptions.RecaptchaMinScore"/>
    /// (<c>Onboarding:RecaptchaMinScore</c>, default 0.5).
    /// The JWT has limited scope (<c>onboarding_anon</c>) and should only
    /// be accepted by onboarding-specific endpoints.
    /// </summary>
    /// <param name="request">The request body containing the reCAPTCHA token.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK. The signed onboarding JWT is set as an httpOnly cookie.</returns>
    [HttpPost("auth/onboarding-token")]
    [AllowAnonymous]
    [ProducesResponseType(200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 422)]
    [ProducesResponseType(typeof(ProblemDetails), 502)]
    public async Task<IActionResult> IssueOnboardingTokenAsync(
        [FromBody] AuthTokenRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RecaptchaToken))
        {
            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: "El token de reCAPTCHA es obligatorio.");
        }

        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available for onboarding token issuance");
            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();

        var recaptchaResult = await _recaptchaVerifier.VerifyAsync(
            request.RecaptchaToken,
            tenantContext,
            remoteIp,
            cancellationToken);

        // A Google outage throws RecaptchaException, which GlobalExceptionHandler answers with 502.
        if (!recaptchaResult.Success || recaptchaResult.Score < _onboardingOptions.RecaptchaMinScore)
        {
            _logger.LogWarning(
                "reCAPTCHA verification failed for tenant {TenantId}. Success: {Success}, Score: {Score}",
                tenantContext.TenantId,
                recaptchaResult.Success,
                recaptchaResult.Score);

            var pd = new ProblemDetails
            {
                Status   = 422,
                Title    = "Unprocessable Entity",
                Detail   = "Falló la verificación de reCAPTCHA.",
                Type     = "https://serviciosfinancieros.api/errors/recaptcha_failed",
                Instance = HttpContext.Request.Path
            };
            pd.Extensions["errorCode"] = "recaptcha_failed";
            return StatusCode(422, pd);
        }

        var token = await _onboardingTokenIssuer.IssueAsync(tenantContext, cancellationToken);

        _logger.LogInformation(
            "Onboarding JWT issued for tenant {TenantId}",
            tenantContext.TenantId);

        return Ok();
    }

    /// <summary>
    /// Returns the full identity profile of the currently authenticated user.
    /// Requires a valid internal JWT. Updates the last-login timestamp
    /// in a fire-and-forget fashion so the response is not delayed.
    /// </summary>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="AuthenticatedUserResponse"/> on success.</returns>
    [HttpGet("auth/me")]
    [Authorize(AuthenticationSchemes = "Internal")]
    [ProducesResponseType(typeof(AuthenticatedUserResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    public async Task<IActionResult> MeAsync(CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in MeAsync");
            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        var uid = User.FindFirst("uid")?.Value;
        if (string.IsNullOrWhiteSpace(uid))
        {
            return Unauthorized();
        }

        var user = await _userRepository.GetByFirebaseUidAsync(uid, cancellationToken)
            .ConfigureAwait(false);

        if (user is null)
        {
            return Problem(
                statusCode: 404,
                title: "Not Found",
                detail: "No se encontró el perfil del usuario.");
        }

        if (!user.IsActive)
        {
            return Problem(
                statusCode: 403,
                title: "Forbidden",
                detail: "La cuenta está inactiva.");
        }

        // Key settings and logging on the RESOLVED tenant (tenantContext.TenantId, from the
        // X-Tenant-ID header). user.TenantId is a separate copy on the Users row and can differ
        // from the resolved tenant; all business/config data (CreditLines, ScoringConfig,
        // TenantSettings) is keyed by the resolved tenant, so reading the mode by user.TenantId
        // could fetch the wrong tenant's setting.
        _logger.LogInformation(
            "Profile retrieved for tenant {TenantId} uid {Uid}",
            tenantContext.TenantId,
            uid);

        // Read the credit-line mode BEFORE dispatching the fire-and-forget last-login update.
        // Both queries share the same scoped FinanciaDbContext (one connection, MARS off), so
        // if the non-awaited update is still in flight this read is a second command on that
        // connection and SQL Server rejects it -> 500. Ordering the read first guarantees no
        // command overlaps the background update.
        var mode = await _tenantSettingsService
            .GetCreditLineModeAsync(tenantContext.TenantId, cancellationToken)
            .ConfigureAwait(false);

        // Same constraint: read before the fire-and-forget update below. Fails open to null.
        var tenantDisplayName = await _tenantSettingsService
            .GetTenantDisplayNameAsync(tenantContext.TenantId, cancellationToken)
            .ConfigureAwait(false);

        // Fire-and-forget: update last login without delaying the response. Dispatched LAST so
        // nothing else issues a command on the shared connection while this one is in flight.
        _ = _userRepository.UpdateLastLoginAsync(user.UserId, CancellationToken.None)
            .ContinueWith(
                t => _logger.LogWarning(
                    t.Exception?.GetBaseException(),
                    "Failed to update last login for user {UserId}",
                    user.UserId),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);

        return Ok(new AuthenticatedUserResponse
        {
            UserId      = user.UserId,
            TenantId    = user.TenantId,
            TenantCode  = tenantContext.TenantId,
            Email       = user.Email,
            DisplayName = user.DisplayName,
            Role        = user.Role,
            FirebaseUid = user.FirebaseUid,
            CreditLineMode = mode,
            TenantDisplayName = tenantDisplayName
        });
    }

    /// <summary>
    /// Silently refreshes the internal JWT session.
    /// Verifies the user still exists and is active, then issues a new
    /// <c>bff_session</c> cookie. Returns no body — the cookie is the response.
    /// Called by the axios interceptor on the frontend when a 401 is received.
    /// </summary>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>204 No Content on success.</returns>
    [HttpPost("auth/refresh")]
    [Authorize(AuthenticationSchemes = "Internal")]
    [ProducesResponseType(204)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> RefreshAsync(CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in RefreshAsync");
            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        var uid = User.FindFirst("uid")?.Value;
        if (string.IsNullOrWhiteSpace(uid))
        {
            return Unauthorized();
        }

        var user = await _userRepository.GetByFirebaseUidAsync(uid, cancellationToken)
            .ConfigureAwait(false);

        if (user is null || !user.IsActive)
        {
            return Unauthorized();
        }

        var token = await _tokenService.IssueAsync(uid, tenantContext, user.UserId, user.Role, cancellationToken)
            .ConfigureAwait(false);

        SetSessionCookie(token);

        _logger.LogInformation(
            "Session refreshed for tenant {TenantId} uid {Uid}",
            tenantContext.TenantId,
            uid);

        return NoContent();
    }

    /// <summary>
    /// Clears the <c>bff_session</c> cookie, effectively logging the user out.
    /// Intentionally <see cref="AllowAnonymousAttribute"/> so the cookie is always
    /// cleared even when the JWT has already expired.
    /// </summary>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>204 No Content always.</returns>
    [HttpPost("auth/logout")]
    [AllowAnonymous]
    [ProducesResponseType(204)]
    public Task<IActionResult> LogoutAsync(CancellationToken cancellationToken)
    {
        Response.Cookies.Delete(SessionCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure   = true,
            SameSite = SameSiteMode.None,
            Path     = "/"
        });

        var uid = User.FindFirst("uid")?.Value;
        _logger.LogInformation("User logged out. Uid: {Uid}", uid ?? "unknown");

        return Task.FromResult<IActionResult>(NoContent());
    }

    /// <summary>
    /// Validates an invitation token and returns basic invitation info.
    /// Used by the frontend to pre-fill the registration form.
    /// </summary>
    /// <param name="token">The invitation token from the email link.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with email, role, expiresAt; 404 if invalid/expired.</returns>
    [HttpGet("auth/validate-token")]
    [AllowAnonymous]
    [ProducesResponseType(200)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> ValidateInviteTokenAsync(
        [FromQuery] string? token,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return BadRequest(new { message = "El parámetro token es obligatorio." });
        }

        var invitation = await _userInvitationService
            .GetInvitationByTokenAsync(token, cancellationToken)
            .ConfigureAwait(false);

        if (invitation is null)
        {
            return NotFound(new { message = "El token de invitación no es válido o expiró." });
        }

        return Ok(new
        {
            email = invitation.Email,
            role = invitation.Role,
            expiresAt = invitation.ExpiresAt
        });
    }

    private void SetSessionCookie(string token)
    {
        // Manually build Set-Cookie header to guarantee SameSite=None; Secure
        // reaches the browser even when Azure App Service rewrites cookie attributes.
        var maxAge = (int)TimeSpan.FromMinutes(15).TotalSeconds;
        var cookieHeader = $"{SessionCookieName}={token}; Path=/; HttpOnly; Secure; SameSite=None; Max-Age={maxAge}";
        Response.Headers.Append("Set-Cookie", cookieHeader);

        _logger.LogDebug("Set-Cookie header written manually for {CookieName}", SessionCookieName);
    }
}
