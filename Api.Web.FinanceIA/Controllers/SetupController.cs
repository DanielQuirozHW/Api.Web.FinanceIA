// Project : Api.Web.FinanceIA
// Created : 2026-10-25

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Clases.Filters;
using Api.Web.FinanceIA.Models.Settings;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// Operator-only setup of a new client. Today one endpoint: inviting the FIRST administrator of a
/// tenant whose database has no active Admin — the only state in which nobody can send the normal
/// invitation (POST /api/bff/settings/users/invite is Admin-only). Off unless the
/// <c>Setup:BootstrapKey</c> App Setting is set; see docs/guides/first-admin.md.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/bff/setup")]
public sealed class SetupController : ControllerBase
{
    private readonly IUserInvitationService _invitationService;
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly ILogger<SetupController> _logger;

    public SetupController(
        IUserInvitationService invitationService,
        ITenantContextAccessor tenantContextAccessor,
        ILogger<SetupController> logger)
    {
        _invitationService = invitationService;
        _tenantContextAccessor = tenantContextAccessor;
        _logger = logger;
    }

    /// <summary>
    /// Creates an Admin invitation with no inviter and sends the normal invitation email.
    /// </summary>
    /// <remarks>
    /// Order of the answers, each one before the next is evaluated:
    /// 404 endpoint off · 401 bad <c>X-Setup-Key</c> (both in <see cref="SetupKeyGateFilter"/>, before
    /// the body is read) · 400 no <c>X-Tenant-ID</c> header or bad email · 409 the tenant has an active
    /// Admin (<c>admin_already_exists</c>), the address has a user (<c>user_already_exists</c>) or an
    /// admin-sent live invitation (<c>invitation_pending</c>) · 201 created. The tenant comes ONLY from
    /// the X-Tenant-ID header — never from a session cookie's JWT. Rate-limited per IP
    /// (<c>RateLimiting:PerIP:FirstAdminSetup</c>, default 5/min).
    /// </remarks>
    [HttpPost("first-admin")]
    [SetupKeyGate]
    [ProducesResponseType(typeof(FirstAdminInvitationResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> InviteFirstAdminAsync(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] FirstAdminSetupRequest? request,
        CancellationToken cancellationToken)
    {
        // Header, not just a TenantContext: without the header TenantFromJwtMiddleware may have
        // resolved the tenant from whatever session cookie the caller happens to hold.
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null || string.IsNullOrWhiteSpace(Request.Headers["X-Tenant-ID"].ToString()))
        {
            return ProblemWithCode(
                StatusCodes.Status400BadRequest,
                "Bad Request",
                TenantErrorMessages.InvalidOrMissingIdentifier,
                "tenant_resolution_error");
        }

        var tenantId = tenantContext.TenantId;

        if (string.IsNullOrWhiteSpace(request?.Email))
        {
            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: "El email es obligatorio.");
        }

        if (!EmailAddressValidator.IsValid(request.Email))
        {
            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: "El formato del email no es válido.");
        }

        FirstAdminInviteOutcome outcome;
        try
        {
            outcome = await _invitationService
                .InviteFirstAdminAsync(
                    tenantId,
                    request.Email,
                    HttpContext.Connection.RemoteIpAddress?.ToString(),
                    HttpContext.TraceIdentifier,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvitationConflictException ex)
        {
            _logger.LogWarning(
                "First-admin invitation refused: {ErrorCode}, TenantId: {TenantId}",
                ex.ErrorCode,
                tenantId);

            return ProblemWithCode(StatusCodes.Status409Conflict, "Conflict", ex.Message, ex.ErrorCode);
        }
        catch (DbUpdateException)
        {
            // The likeliest cause in production: DDL 075 has not run on this tenant's database, so
            // the NULL inviter is rejected — and --verify-schema cannot catch that (names only).
            _logger.LogError(
                "First-admin invitation insert failed, TenantId: {TenantId}. " +
                "Check that DDL 075_user_invitations_inviter_nullable.sql ran on this database.",
                tenantId);
            throw;
        }

        if (outcome.AdminAlreadyExists || outcome.Invitation is null)
        {
            _logger.LogWarning(
                "First-admin invitation refused: tenant already has an active Admin, TenantId: {TenantId}",
                tenantId);

            return ProblemWithCode(
                StatusCodes.Status409Conflict,
                "Conflict",
                "La organización ya tiene un administrador.",
                "admin_already_exists");
        }

        return StatusCode(StatusCodes.Status201Created, outcome.Invitation);
    }

    private ObjectResult ProblemWithCode(int status, string title, string detail, string errorCode)
    {
        var problem = new ProblemDetails
        {
            Status   = status,
            Title    = title,
            Detail   = detail,
            Type     = $"https://serviciosfinancieros.api/errors/{errorCode}",
            Instance = HttpContext.Request.Path
        };
        problem.Extensions["errorCode"] = errorCode;
        return StatusCode(status, problem);
    }
}
