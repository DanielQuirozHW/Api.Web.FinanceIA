// Project : Api.Web.FinanceIA
// Created : 2026-05-03
// Modified: 2026-10-26

using System.Security.Claims;
using FirebaseAdmin.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.EmailVerification;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Entities;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// Handles the anonymous side of the user invitation flow: validating invite tokens and
/// registering invited users. Sending an invitation lives in <see cref="SettingsController"/>
/// (POST /api/bff/settings/users/invite, Admin only).
/// </summary>
[ApiController]
[Route("api/bff")]
public sealed class InvitationController : ControllerBase
{
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly IFirebaseTokenValidator _firebaseTokenValidator;
    private readonly ITokenService _tokenService;
    private readonly IUserRepository _userRepository;
    private readonly IUserInvitationRepository _invitationRepository;
    private readonly IAuditService _auditService;
    private readonly ITenantSettingsService _tenantSettingsService;
    private readonly ITenantFirebaseUserAdmin _firebaseUserAdmin;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<InvitationController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="InvitationController"/>.
    /// </summary>
    public InvitationController(
        ITenantContextAccessor tenantContextAccessor,
        IFirebaseTokenValidator firebaseTokenValidator,
        ITokenService tokenService,
        IUserRepository userRepository,
        IUserInvitationRepository invitationRepository,
        IAuditService auditService,
        ITenantSettingsService tenantSettingsService,
        ITenantFirebaseUserAdmin firebaseUserAdmin,
        IWebHostEnvironment environment,
        ILogger<InvitationController> logger)
    {
        _tenantContextAccessor    = tenantContextAccessor;
        _firebaseTokenValidator   = firebaseTokenValidator;
        _tokenService             = tokenService;
        _userRepository           = userRepository;
        _invitationRepository     = invitationRepository;
        _auditService             = auditService;
        _tenantSettingsService    = tenantSettingsService;
        _firebaseUserAdmin        = firebaseUserAdmin;
        _environment              = environment;
        _logger                   = logger;
    }

    /// <summary>
    /// Validates an invitation token and returns the invitation details.
    /// Anonymous endpoint — does not reveal whether the token expired,
    /// was already used, or never existed.
    /// </summary>
    /// <param name="token">The opaque invitation token from the email link.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="InviteInfoResponse"/> when the token is valid.</returns>
    [HttpGet("auth/invite/{token}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(InviteInfoResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    public async Task<IActionResult> GetInviteInfoAsync(
        string token,
        CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in GetInviteInfoAsync");
            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        UserInvitation? invitation = await _invitationRepository
            .GetByTokenAsync(token, tenantContext.TenantId, cancellationToken)
            .ConfigureAwait(false);

        if (invitation is null)
        {
            var pd = new ProblemDetails
            {
                Status   = 404,
                Title    = "Not Found",
                Detail   = "No se encontró la invitación.",
                Type     = "https://serviciosfinancieros.api/errors/invitation_not_found",
                Instance = HttpContext.Request.Path
            };
            pd.Extensions["errorCode"] = "invitation_not_found";
            return StatusCode(404, pd);
        }

        return Ok(new InviteInfoResponse(
            Email:      invitation.Email,
            Role:       invitation.Role,
            TenantCode: tenantContext.TenantId));
    }

    /// <summary>
    /// Registers a new user who holds a valid invitation.
    /// Validates the Firebase ID token from the Authorization header,
    /// verifies the invite token, creates the user account with the role
    /// defined in the invitation, and issues an internal JWT session.
    /// <para>
    /// This is the ONLY endpoint that creates a Users row. The uid and email come from the
    /// validated Firebase token, never the body; the invitation must be live (not used, revoked
    /// or expired) and belong to the resolved tenant; its email must match the token's; and the
    /// role is the invitation's. <c>email_verified</c> is deliberately not required: the Portal
    /// creates the Firebase account immediately before this call, so it is never verified yet.
    /// </para>
    /// <para>
    /// Holding the emailed invite token IS the proof of mailbox control, so once the Users row exists
    /// this endpoint sets <c>EmailVerified = true</c> on the Firebase user with the tenant's app. That
    /// step is fault-isolated: a Firebase failure logs a Warning (UserId and TenantId only) and the
    /// registration still succeeds. <see cref="AuthenticatedUserResponse.EmailVerified"/> says which
    /// happened — when true the Portal reloads its Firebase user and sends no verification mail;
    /// when false the user can ask for one through POST api/bff/auth/email-verification.
    /// </para>
    /// </summary>
    /// <param name="request">Invite token and display name.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="AuthenticatedUserResponse"/> on success.</returns>
    [HttpPost("auth/register-invited")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthenticatedUserResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> RegisterInvitedAsync(
        [FromBody] RegisterInvitedRequest request,
        CancellationToken cancellationToken)
    {
        // Extract Firebase Bearer token from Authorization header
        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer "))
        {
            return Problem(
                statusCode: 401,
                title: "Unauthorized",
                detail: "El token de ID de Firebase es obligatorio.");
        }

        var firebaseToken = authHeader["Bearer ".Length..].Trim();

        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in RegisterInvitedAsync");
            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        // A blank token would make GetByTokenAsync throw and a null name would NRE — both 500s.
        if (string.IsNullOrWhiteSpace(request.InviteToken)
            || string.IsNullOrWhiteSpace(request.DisplayName)
            || request.DisplayName.Trim().Length > 200)
        {
            var pd = new ProblemDetails
            {
                Status   = 400,
                Title    = "Bad Request",
                Detail   = "La invitación y un nombre de hasta 200 caracteres son obligatorios.",
                Type     = "https://serviciosfinancieros.api/errors/invalid_request",
                Instance = HttpContext.Request.Path
            };
            pd.Extensions["errorCode"] = "invalid_request";
            return StatusCode(400, pd);
        }

        // Validate Firebase token — FirebaseAuthException propagates to GlobalExceptionHandler → 401
        FirebaseToken validated = await _firebaseTokenValidator
            .ValidateAsync(firebaseToken, tenantContext, cancellationToken)
            .ConfigureAwait(false);

        var firebaseUid   = validated.Uid;
        var firebaseEmail = (validated.Claims.TryGetValue("email", out object? emailObj)
                             ? emailObj?.ToString()
                             : null)?.ToLowerInvariant()
                            ?? string.Empty;

        // Validate invite token
        UserInvitation? invitation = await _invitationRepository
            .GetByTokenAsync(request.InviteToken, tenantContext.TenantId, cancellationToken)
            .ConfigureAwait(false);

        if (invitation is null)
        {
            var pd = new ProblemDetails
            {
                Status   = 400,
                Title    = "Bad Request",
                Detail   = "Invitación inválida o expirada.",
                Type     = "https://serviciosfinancieros.api/errors/invitation_invalid",
                Instance = HttpContext.Request.Path
            };
            pd.Extensions["errorCode"] = "invitation_invalid";
            return StatusCode(400, pd);
        }

        // Email must match the invitation
        if (!string.Equals(firebaseEmail, invitation.Email, StringComparison.OrdinalIgnoreCase))
        {
            var pd = new ProblemDetails
            {
                Status   = 400,
                Title    = "Bad Request",
                Detail   = "El email no coincide con la invitación.",
                Type     = "https://serviciosfinancieros.api/errors/email_mismatch",
                Instance = HttpContext.Request.Path
            };
            pd.Extensions["errorCode"] = "email_mismatch";
            return StatusCode(400, pd);
        }

        // Users.FirebaseUid and Users.Email are unique: answer 409 instead of letting the insert
        // fail with a 500, and leave the invitation unconsumed.
        bool uidTaken = await _userRepository
            .ExistsByFirebaseUidAsync(firebaseUid, cancellationToken)
            .ConfigureAwait(false);

        bool emailTaken = !uidTaken
            && !string.IsNullOrWhiteSpace(firebaseEmail)
            && await _userRepository
            .GetByEmailAsync(firebaseEmail, cancellationToken)
            .ConfigureAwait(false) is not null;

        if (uidTaken || emailTaken)
        {
            _logger.LogWarning(
                "Invited registration rejected: user already registered. TenantId: {TenantId} InvitationId: {InvitationId}",
                tenantContext.TenantId,
                invitation.InvitationId);

            var pd = new ProblemDetails
            {
                Status   = 409,
                Title    = "Conflict",
                Detail   = "El usuario ya está registrado.",
                Type     = "https://serviciosfinancieros.api/errors/user_already_registered",
                Instance = HttpContext.Request.Path
            };
            pd.Extensions["errorCode"] = "user_already_registered";
            return StatusCode(409, pd);
        }

        var userId = Guid.NewGuid();
        var now    = DateTime.UtcNow;

        var user = new Models.Financia.User
        {
            UserId             = userId,
            TenantId           = tenantContext.TenantId,
            FirebaseUid        = firebaseUid,
            Email              = firebaseEmail,
            DisplayName        = request.DisplayName.Trim(),
            Role               = invitation.Role,
            IsActive           = true,
            MustChangePassword = false,
            TwoFactorEnabled   = false,
            FailedLoginCount   = 0,
            CreatedAt          = now,
            UpdatedAt          = now,
            CreatedBy          = null
        };

        // Step 6: create user — if this throws, invitation is NOT marked as used
        await _userRepository.CreateAsync(user, cancellationToken).ConfigureAwait(false);

        // Step 7: mark invitation as used — failure is logged but does not affect the response
        try
        {
            await _invitationRepository
                .MarkAsUsedAsync(invitation.InvitationId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to mark invitation as used after user creation. " +
                "TenantId: {TenantId} InvitationId: {InvitationId} UserId: {UserId}",
                tenantContext.TenantId,
                invitation.InvitationId,
                userId);
        }

        // Step 8: mark the Firebase email verified — failure is logged but does not affect the response
        var emailVerified = await TryMarkEmailVerifiedAsync(firebaseUid, userId, tenantContext, cancellationToken)
            .ConfigureAwait(false);

        var remoteIp  = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = HttpContext.Request.Headers.UserAgent.ToString();

        // Fire-and-forget session log
        Guid.TryParse(tenantContext.TenantId, out var auditTenantId2);
        _ = _auditService.LogSessionAsync(
            tenantId:  auditTenantId2,
            userId:    userId,
            email:     firebaseEmail,
            eventType: "Login.Success",
            ipAddress: remoteIp,
            userAgent: userAgent,
            ct:        CancellationToken.None);

        // Fire-and-forget audit log
        _ = _auditService.LogAsync(
            tenantId:   tenantContext.TenantId,
            userId:     userId,
            action:     "User.RegisteredViaInvitation",
            severity:   "Info",
            entityType: "User",
            entityId:   userId.ToString(),
            ipAddress:  remoteIp,
            requestId:  HttpContext.TraceIdentifier,
            ct:         CancellationToken.None);

        var jwtToken = await _tokenService
            .IssueAsync(
                firebaseUid,
                tenantContext,
                userId,
                user.Role,
                cancellationToken)
            .ConfigureAwait(false);

        Response.Cookies.Append("bff_session", jwtToken, new CookieOptions
        {
            HttpOnly = true,
            Secure   = true,
            SameSite = SameSiteMode.None,
            MaxAge   = TimeSpan.FromMinutes(15),
            Path     = "/"
        });

        _logger.LogInformation(
            "Invited user registered for tenant {TenantId} uid {Uid} role {Role}",
            tenantContext.TenantId,
            firebaseUid,
            user.Role);

        var mode = await _tenantSettingsService
            .GetCreditLineModeAsync(tenantContext.TenantId, cancellationToken)
            .ConfigureAwait(false);

        var tenantDisplayName = await _tenantSettingsService
            .GetTenantDisplayNameAsync(tenantContext.TenantId, cancellationToken)
            .ConfigureAwait(false);

        return Ok(new AuthenticatedUserResponse
        {
            UserId      = userId,
            TenantId    = tenantContext.TenantId,
            TenantCode  = tenantContext.TenantId,
            Email       = user.Email,
            DisplayName = user.DisplayName,
            Role        = user.Role,
            FirebaseUid = firebaseUid,
            CreditLineMode = mode,
            TenantDisplayName = tenantDisplayName,
            EmailVerified = emailVerified
        });
    }

    /// <summary>
    /// Sets <c>EmailVerified</c> on the invited user's Firebase account. Never throws: the Users row
    /// already exists, so a Firebase failure must not turn a completed registration into an error.
    /// The exception is logged by TYPE only — Firebase messages can echo the address.
    /// </summary>
    private async Task<bool> TryMarkEmailVerifiedAsync(
        string firebaseUid,
        Guid userId,
        TenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        try
        {
            await _firebaseUserAdmin
                .MarkEmailVerifiedAsync(firebaseUid, tenantContext, cancellationToken)
                .ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Could not mark the invited user's email verified in Firebase. " +
                "TenantId: {TenantId} UserId: {UserId} ExceptionType: {ExceptionType}",
                tenantContext.TenantId,
                userId,
                ex.GetType().Name);

            return false;
        }
    }
}
