// Project : Api.Web.FinanceIA
// Created : 2026-10-21

using System.Diagnostics;
using System.Globalization;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.EmailVerification;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// Asks the BFF to email the signed-in user a link that verifies their address. The link is
/// generated with the tenant's Firebase Admin app and sent through SendGrid with the tenant's
/// template and sender, replacing Firebase's stock <c>sendEmailVerification</c>; the Portal's
/// /verify-email route applies the code. Invited users never need it — register-invited marks them
/// verified — so this serves everyone else.
/// </summary>
/// <remarks>
/// Authenticated and about the CURRENT session user only: there is no body, and the address comes
/// from the Users row, never from the request. X-Tenant-ID is required like on every
/// /api/bff/auth/* route. The Firebase link and SendGrid run in <see cref="EmailVerificationWorker"/>
/// after the 202, as for password reset.
/// </remarks>
[ApiController]
[Route("api/bff")]
[Authorize(AuthenticationSchemes = "Internal")]
public sealed class EmailVerificationController : ControllerBase
{
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly IUserRepository _userRepository;
    private readonly ITenantFirebaseUserAdmin _firebaseUserAdmin;
    private readonly IEmailVerificationThrottle _throttle;
    private readonly IEmailVerificationQueue _queue;
    private readonly ILogger<EmailVerificationController> _logger;

    /// <summary>Initializes a new instance of <see cref="EmailVerificationController"/>.</summary>
    public EmailVerificationController(
        ITenantContextAccessor tenantContextAccessor,
        IUserRepository userRepository,
        ITenantFirebaseUserAdmin firebaseUserAdmin,
        IEmailVerificationThrottle throttle,
        IEmailVerificationQueue queue,
        ILogger<EmailVerificationController> logger)
    {
        _tenantContextAccessor = tenantContextAccessor;
        _userRepository        = userRepository;
        _firebaseUserAdmin     = firebaseUserAdmin;
        _throttle              = throttle;
        _queue                 = queue;
        _logger                = logger;
    }

    /// <summary>
    /// Requests a verification email for the signed-in user.
    /// </summary>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>
    /// 200 <c>{ alreadyVerified: true }</c> when Firebase already marks the address verified (nothing
    /// sent); 202 <c>{ alreadyVerified: false }</c> when the email was queued; 401 without a usable
    /// userId claim; 403 for an inactive user; 404 when the user is not in this tenant; 429 with
    /// Retry-After when the user used up the window; 502 when Firebase cannot be reached.
    /// </returns>
    [HttpPost("auth/email-verification")]
    [ProducesResponseType(typeof(EmailVerificationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(EmailVerificationResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> RequestEmailVerificationAsync(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma       = "no-cache";

        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available for email verification");

            return Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title     : "Internal Server Error",
                detail    : "Tenant context not resolved.");
        }

        var claim = User.FindFirst("userId")?.Value ?? User.FindFirst("sub")?.Value;
        if (!Guid.TryParse(claim, out var userId))
        {
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title     : "Unauthorized",
                detail    : "La sesión no identifica a un usuario.");
        }

        var tenantId = tenantContext.TenantId;

        // 1. Budget, counted for every request before Firebase is asked anything.
        var throttle = _throttle.Register(tenantId, userId);
        if (!throttle.IsAllowed)
        {
            _logger.LogWarning(
                "Email verification per-user limit exceeded for tenant {TenantId} user {UserId}",
                tenantId,
                userId);

            Response.Headers.RetryAfter = throttle.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return ErrorProblem(
                StatusCodes.Status429TooManyRequests,
                "Too Many Requests",
                $"Se superó el límite de solicitudes. Intentá de nuevo en {throttle.RetryAfterSeconds} segundos.",
                "too_many_requests",
                throttle.RetryAfterSeconds);
        }

        // 2. The user, in THIS tenant.
        var user = await _userRepository.GetByUserIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null || !string.Equals(user.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
        {
            return Problem(
                statusCode: StatusCodes.Status404NotFound,
                title     : "Not Found",
                detail    : "No se encontró el perfil del usuario.");
        }

        if (!user.IsActive)
        {
            return Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title     : "Forbidden",
                detail    : "La cuenta está inactiva.");
        }

        // 3. Already verified: nothing to send. A Firebase failure throws FirebaseServiceException,
        //    which GlobalExceptionHandler answers with 502.
        var isVerified = await _firebaseUserAdmin
            .GetEmailVerifiedAsync(user.FirebaseUid, tenantContext, cancellationToken)
            .ConfigureAwait(false);

        if (isVerified == true)
        {
            _logger.LogInformation(
                "Email verification {Outcome} for tenant {TenantId} user {UserId}",
                EmailVerificationOutcome.AlreadyVerified,
                tenantId,
                userId);

            return Ok(new EmailVerificationResponse { AlreadyVerified = true });
        }

        // 4. Hand off. The Firebase link and SendGrid happen after this response.
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        var traceId  = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
        if (!_queue.TryEnqueue(new EmailVerificationWorkItem(tenantContext, userId, user.Email, remoteIp, traceId)))
        {
            _logger.LogWarning(
                "Email verification queue full; request dropped for tenant {TenantId} user {UserId}",
                tenantId,
                userId);
        }
        else
        {
            _logger.LogInformation(
                "Email verification requested for tenant {TenantId} user {UserId}",
                tenantId,
                userId);
        }

        return StatusCode(StatusCodes.Status202Accepted, new EmailVerificationResponse { AlreadyVerified = false });
    }

    private ObjectResult ErrorProblem(int status, string title, string detail, string errorCode, int retryAfter)
    {
        var problemDetails = new ProblemDetails
        {
            Status   = status,
            Title    = title,
            Detail   = detail,
            Type     = $"https://serviciosfinancieros.api/errors/{errorCode}",
            Instance = HttpContext.Request.Path
        };
        problemDetails.Extensions["errorCode"]  = errorCode;
        problemDetails.Extensions["retryAfter"] = retryAfter;

        return StatusCode(status, problemDetails);
    }
}
