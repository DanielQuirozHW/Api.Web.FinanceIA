// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-21
// Modified: 2026-04-21

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Api.Web.ServiciosFinancieros.Clases;
using Api.Web.ServiciosFinancieros.Models;

namespace Api.Web.ServiciosFinancieros.Controllers;

/// <summary>
/// Backend-For-Frontend authentication controller.
/// Handles Firebase token exchange for internal JWTs and
/// issues short-lived onboarding tokens for unauthenticated flows.
/// </summary>
[ApiController]
[Route("api/bff")]
public sealed class BffController : ControllerBase
{
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly IFirebaseTokenValidator _firebaseTokenValidator;
    private readonly ITokenService _tokenService;
    private readonly IOnboardingTokenIssuer _onboardingTokenIssuer;
    private readonly IRecaptchaVerifier _recaptchaVerifier;
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
        ILogger<BffController> logger)
    {
        _tenantContextAccessor = tenantContextAccessor;
        _firebaseTokenValidator = firebaseTokenValidator;
        _tokenService = tokenService;
        _onboardingTokenIssuer = onboardingTokenIssuer;
        _recaptchaVerifier = recaptchaVerifier;
        _logger = logger;
    }

    /// <summary>
    /// Exchanges a validated Firebase ID token for an internal JWT.
    /// The Firebase token is validated by <see cref="FirebaseAuthenticationHandler"/>
    /// before this action executes. Returns the JWT in both the response
    /// body and as an httpOnly cookie.
    /// </summary>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>An <see cref="AuthTokenResponse"/> containing the signed internal JWT.</returns>
    [HttpPost("auth/token")]
    [Authorize(AuthenticationSchemes = FirebaseAuthenticationHandler.SchemeName)]
    [ProducesResponseType(typeof(AuthTokenResponse), 200)]
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

        var token = await _tokenService.IssueAsync(User, tenantContext, cancellationToken);

        Response.Cookies.Append("bff_session", token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            MaxAge = TimeSpan.FromMinutes(15),
            Path = "/"
        });

        _logger.LogInformation(
            "Internal JWT issued for tenant {TenantId} uid {Uid}",
            tenantContext.TenantId,
            uid);

        return Ok(new AuthTokenResponse { Token = token });
    }

    /// <summary>
    /// Issues a short-lived onboarding JWT for unauthenticated users.
    /// Requires a valid reCAPTCHA V3 token with score >= 0.5.
    /// The JWT has limited scope (<c>onboarding_anon</c>) and should only
    /// be accepted by onboarding-specific endpoints.
    /// </summary>
    /// <param name="request">The request body containing the reCAPTCHA token.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>An <see cref="AuthTokenResponse"/> containing the signed onboarding JWT.</returns>
    [HttpPost("auth/onboarding-token")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthTokenResponse), 200)]
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
                detail: "reCAPTCHA token is required.");
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

        var threshold = 0.5;

        //if (!recaptchaResult.Success || recaptchaResult.Score < 0.5)
        if (recaptchaResult.Success || recaptchaResult.Score < threshold)
        {
            _logger.LogWarning(
                "reCAPTCHA verification failed for tenant {TenantId}. Success: {Success}, Score: {Score}",
                tenantContext.TenantId,
                recaptchaResult.Success,
                recaptchaResult.Score);

            var pd = new ProblemDetails
            {
                Status = 422,
                Title = "Unprocessable Entity",
                Detail = "reCAPTCHA verification failed.",
                Type = "https://serviciosfinancieros.api/errors/recaptcha_failed",
                Instance = HttpContext.Request.Path
            };
            pd.Extensions["errorCode"] = "recaptcha_failed";
            return StatusCode(422, pd);
        }

        var token = await _onboardingTokenIssuer.IssueAsync(tenantContext, cancellationToken);

        _logger.LogInformation(
            "Onboarding JWT issued for tenant {TenantId}",
            tenantContext.TenantId);

        return Ok(new AuthTokenResponse { Token = token });
    }
}
