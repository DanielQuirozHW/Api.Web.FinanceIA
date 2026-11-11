// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using System.Diagnostics;
using System.Net.Mail;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.PasswordReset;
using Api.Web.FinanceIA.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// Step 1 of the Portal's /recover flow: asks the BFF to email a password-reset link. The link is
/// generated with the tenant's Firebase Admin app and sent through SendGrid with the tenant's
/// template and sender, replacing Firebase's stock <c>sendPasswordResetEmail</c>. Steps 2–3
/// (<c>confirmPasswordReset</c>) stay in the Portal.
/// </summary>
/// <remarks>
/// <para>
/// Anonymous by construction — the user cannot sign in. X-Tenant-ID is still required:
/// TenantResolutionMiddleware rejects any /api/bff/auth/* request without it.
/// </para>
/// <para>
/// No account enumeration. Every well-formed request that passes reCAPTCHA and the rate limits gets
/// the SAME 202 and body, and gets it at the same speed: the lookup, Firebase and SendGrid run in
/// <see cref="PasswordResetWorker"/> after the response. The only distinct answers (400, 403, 429,
/// 502) depend on the request itself or on our infrastructure, never on whether the address exists.
/// </para>
/// </remarks>
[ApiController]
[Route("api/bff")]
public sealed class PasswordResetController : ControllerBase
{
    private const int MaxEmailLength = 256; // Users.Email is nvarchar(256)

    private const string AcceptedMessage =
        "Si el correo corresponde a una cuenta activa, vas a recibir un enlace para restablecer tu contraseña.";

    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly IRecaptchaVerifier _recaptchaVerifier;
    private readonly IPasswordResetEmailThrottle _emailThrottle;
    private readonly IPasswordResetQueue _queue;
    private readonly PasswordResetOptions _options;
    private readonly ILogger<PasswordResetController> _logger;

    /// <summary>Initializes a new instance of <see cref="PasswordResetController"/>.</summary>
    public PasswordResetController(
        ITenantContextAccessor tenantContextAccessor,
        IRecaptchaVerifier recaptchaVerifier,
        IPasswordResetEmailThrottle emailThrottle,
        IPasswordResetQueue queue,
        IOptions<PasswordResetOptions> options,
        ILogger<PasswordResetController> logger)
    {
        _tenantContextAccessor = tenantContextAccessor;
        _recaptchaVerifier     = recaptchaVerifier;
        _emailThrottle         = emailThrottle;
        _queue                 = queue;
        _options               = options.Value;
        _logger                = logger;
    }

    /// <summary>
    /// Requests a password-reset email for <see cref="PasswordResetRequest.Email"/> in the tenant
    /// named by X-Tenant-ID.
    /// </summary>
    /// <param name="request">The address and a reCAPTCHA V3 token.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>
    /// 202 with the same body whether or not the address belongs to an active user; 400 for a
    /// malformed request; 403 when reCAPTCHA fails; 429 when the address used up its window.
    /// </returns>
    [HttpPost("auth/password-reset")]
    [AllowAnonymous]
    [RequestSizeLimit(4 * 1024)]
    [ProducesResponseType(typeof(PasswordResetResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> RequestPasswordResetAsync(
        [FromBody] PasswordResetRequest request,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma       = "no-cache";

        var email = request?.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        if (request is null || string.IsNullOrWhiteSpace(request.RecaptchaToken) || !IsWellFormedEmail(email))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title     : "Bad Request",
                detail    : "Un email válido y el token de reCAPTCHA son obligatorios.");
        }

        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available for password reset");

            return Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title     : "Internal Server Error",
                detail    : "Tenant context not resolved.");
        }

        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();

        // 1. reCAPTCHA, before anything is counted or queued. A Google outage throws
        //    RecaptchaException, which GlobalExceptionHandler answers with 502.
        var recaptchaResult = await _recaptchaVerifier
            .VerifyAsync(request.RecaptchaToken, tenantContext, remoteIp, cancellationToken)
            .ConfigureAwait(false);

        if (!recaptchaResult.Success || recaptchaResult.Score < _options.RecaptchaMinScore)
        {
            _logger.LogWarning(
                "reCAPTCHA verification failed for password reset. " +
                "TenantId: {TenantId} | Success: {Success} | Score: {Score}",
                tenantContext.TenantId,
                recaptchaResult.Success,
                recaptchaResult.Score);

            return ErrorProblem(
                StatusCodes.Status403Forbidden,
                "Forbidden",
                "Falló la verificación de reCAPTCHA.",
                "recaptcha_failed");
        }

        var emailHash = EmailHasher.Hash(email);

        // 2. Per-email budget, counted for EVERY address before any lookup, so a 429 says nothing
        //    about whether the account exists.
        var throttle = _emailThrottle.Register(tenantContext.TenantId, email);
        if (!throttle.IsAllowed)
        {
            _logger.LogWarning(
                "Password reset per-email limit exceeded for tenant {TenantId} email {EmailHash}",
                tenantContext.TenantId,
                emailHash);

            Response.Headers.RetryAfter = throttle.RetryAfterSeconds.ToString();
            return ErrorProblem(
                StatusCodes.Status429TooManyRequests,
                "Too Many Requests",
                $"Se superó el límite de solicitudes para este email. Intentá de nuevo en {throttle.RetryAfterSeconds} segundos.",
                "rate_limit_exceeded_email",
                throttle.RetryAfterSeconds);
        }

        // 3. Hand off. The lookup, Firebase and SendGrid happen after this response.
        var traceId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
        if (!_queue.TryEnqueue(new PasswordResetWorkItem(tenantContext, email, remoteIp, traceId)))
        {
            _logger.LogWarning(
                "Password reset queue full; request dropped for tenant {TenantId} email {EmailHash}",
                tenantContext.TenantId,
                emailHash);
        }
        else
        {
            _logger.LogInformation(
                "Password reset requested for tenant {TenantId} email {EmailHash}",
                tenantContext.TenantId,
                emailHash);
        }

        return StatusCode(StatusCodes.Status202Accepted, new PasswordResetResponse { Message = AcceptedMessage });
    }

    private static bool IsWellFormedEmail(string email)
    {
        if (email.Length == 0 || email.Length > MaxEmailLength)
            return false;

        // The parsed address must be the whole input: MailAddress also accepts display-name forms
        // such as "Name <a@b.c>", which are not an address anyone types on /recover.
        return MailAddress.TryCreate(email, out var parsed)
               && string.Equals(parsed.Address, email, StringComparison.Ordinal);
    }

    private ObjectResult ErrorProblem(int status, string title, string detail, string errorCode, int? retryAfter = null)
    {
        var problemDetails = new ProblemDetails
        {
            Status   = status,
            Title    = title,
            Detail   = detail,
            Type     = $"https://serviciosfinancieros.api/errors/{errorCode}",
            Instance = HttpContext.Request.Path
        };
        problemDetails.Extensions["errorCode"] = errorCode;
        if (retryAfter.HasValue)
        {
            problemDetails.Extensions["retryAfter"] = retryAfter.Value;
        }

        return StatusCode(status, problemDetails);
    }
}
