// Project : Api.Web.FinanceIA
// Created : 2026-05-11

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Clases.PasswordReset;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Settings;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// Settings API for user profile management and admin user operations.
/// All endpoints require an authenticated internal JWT session.
/// </summary>
[Authorize(AuthenticationSchemes = "Internal")]
[ApiController]
[Route("api/bff/settings")]
public sealed class SettingsController : ControllerBase
{
    private static readonly string[] ValidRoles = ["Analyst", "RiskManager", "Admin", "Auditor"];

    private readonly ISettingsService _settingsService;
    private readonly IUserInvitationService _invitationService;
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly IUserRepository _userRepository;
    private readonly IUserApprovalLimitRepository _approvalLimitRepository;
    private readonly ITeamRepository _teamRepository;
    private readonly ISgrSettingsService _sgrSettingsService;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(
        ISettingsService settingsService,
        IUserInvitationService invitationService,
        ITenantContextAccessor tenantContextAccessor,
        IUserRepository userRepository,
        IUserApprovalLimitRepository approvalLimitRepository,
        ITeamRepository teamRepository,
        ISgrSettingsService sgrSettingsService,
        ILogger<SettingsController> logger)
    {
        _settingsService = settingsService;
        _invitationService = invitationService;
        _tenantContextAccessor = tenantContextAccessor;
        _userRepository = userRepository;
        _approvalLimitRepository = approvalLimitRepository;
        _teamRepository = teamRepository;
        _sgrSettingsService = sgrSettingsService;
        _logger = logger;
    }

    /// <summary>
    /// Returns the profile of the currently authenticated user.
    /// </summary>
    [HttpGet("profile")]
    [ProducesResponseType(typeof(ProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProfile(CancellationToken cancellationToken)
    {
        var userId = ExtractUserId();
        var tenantId = ExtractTenantId();

        var profile = await _settingsService
            .GetProfileAsync(userId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (profile is null)
        {
            _logger.LogWarning(
                "Profile not found for UserId: {UserId}, TenantId: {TenantId}",
                userId, tenantId);

            return Problem(
                statusCode: 404,
                title: "Not Found",
                detail: "El perfil de usuario solicitado no existe.");
        }

        return Ok(profile);
    }

    /// <summary>
    /// Updates the display name of the currently authenticated user.
    /// </summary>
    [HttpPut("profile")]
    [ProducesResponseType(typeof(ProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateProfile(
        [FromBody] UpdateProfileRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: "DisplayName es obligatorio.");
        }

        if (request.DisplayName.Trim().Length > 200)
        {
            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: "DisplayName no puede superar los 200 caracteres.");
        }

        var userId = ExtractUserId();
        var tenantId = ExtractTenantId();
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var requestId = HttpContext.TraceIdentifier;

        var updatedProfile = await _settingsService
            .UpdateProfileAsync(userId, tenantId, request, ipAddress, requestId, cancellationToken)
            .ConfigureAwait(false);

        if (updatedProfile is null)
        {
            return Problem(
                statusCode: 404,
                title: "Not Found",
                detail: "El perfil de usuario solicitado no existe.");
        }

        return Ok(updatedProfile);
    }

    /// <summary>
    /// Returns all users belonging to the authenticated user's tenant.
    /// Requires Admin role.
    /// </summary>
    [HttpGet("users")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(List<UserDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUsers(CancellationToken cancellationToken)
    {
        var tenantId = ExtractTenantId();

        var users = await _settingsService
            .GetUsersAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Retrieved {Count} users for TenantId: {TenantId}",
            users.Count,
            tenantId);

        return Ok(users);
    }

    /// <summary>
    /// Returns the approval limits configured for one user — how much they may commit directly,
    /// per currency, before an analysis needs a second signature. Requires Admin role.
    /// <para>
    /// A currency with NO row is simply absent from the list; that is the default and means the user
    /// may only pre-approve in it. An empty list is therefore the normal state of a newly invited
    /// analyst, not a misconfiguration.
    /// </para>
    /// </summary>
    [HttpGet("users/{userId:guid}/approval-limits")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(List<UserApprovalLimitResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUserApprovalLimits(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var tenantId = ExtractTenantId();

        var targetError = await EnsureUserInTenantAsync(userId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (targetError is not null) return targetError;

        var limits = await _approvalLimitRepository
            .GetByUserAsync(userId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        return Ok(limits.Select(MapLimit).ToList());
    }

    /// <summary>
    /// Replaces the complete set of approval limits for one user. Requires Admin role.
    /// <para>
    /// A REPLACE, not a merge: currencies absent from the payload are cleared. An empty list is
    /// legal and returns the user to the default of "may only pre-approve".
    /// </para>
    /// </summary>
    [HttpPut("users/{userId:guid}/approval-limits")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(List<UserApprovalLimitResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetUserApprovalLimits(
        Guid userId,
        [FromBody] SetUserApprovalLimitsRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = ExtractTenantId();

        if (request?.Limits is null)
        {
            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: "El cuerpo debe incluir la lista 'limits'. Enviá una lista vacía para quitar todos los límites.");
        }

        var targetError = await EnsureUserInTenantAsync(userId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (targetError is not null) return targetError;

        var normalised = new List<UserApprovalLimit>(request.Limits.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in request.Limits)
        {
            var currency = (entry.Currency ?? string.Empty).Trim().ToUpperInvariant();

            if (!Currencies.IsKnown(currency))
            {
                return Problem(
                    statusCode: 400,
                    title: "Bad Request",
                    detail: $"La moneda '{entry.Currency}' no es válida. Debe ser una de: {Currencies.KnownList}.");
            }

            // Two entries for one currency would make the stored ceiling depend on list order.
            if (!seen.Add(currency))
            {
                return Problem(
                    statusCode: 400,
                    title: "Bad Request",
                    detail: $"La moneda '{currency}' aparece más de una vez.");
            }

            if (entry.MaxAmount is < 0m)
            {
                return Problem(
                    statusCode: 400,
                    title: "Bad Request",
                    detail: $"El monto máximo para '{currency}' no puede ser negativo.");
            }

            normalised.Add(new UserApprovalLimit
            {
                TenantId   = tenantId,
                UserId     = userId,
                Currency   = currency,
                CanApprove = entry.CanApprove,
                MaxAmount  = entry.MaxAmount,
            });
        }

        var saved = await _approvalLimitRepository
            .ReplaceForUserAsync(userId, tenantId, normalised, ExtractUserId(), cancellationToken)
            .ConfigureAwait(false);

        // The ceilings themselves stay out of the log line — who may commit how much is not
        // application-log material. The audit trail is the DB row, which records UpdatedBy.
        _logger.LogInformation(
            "Approval limits updated for user {UserId}, TenantId: {TenantId}, currencies: {Count}",
            userId,
            tenantId,
            saved.Count);

        return Ok(saved.Select(MapLimit).ToList());
    }

    /// <summary>
    /// Assigns a user to a team, or unassigns them.
    /// Requires Admin role.
    /// </summary>
    /// <remarks>
    /// Folded into this per-user admin surface rather than given a route of its own, beside
    /// <c>approval-limits</c>: both answer "what is this user allowed to do, and who settles it when
    /// they cannot", both are Admin-only, and both need the same cross-tenant guard.
    ///
    /// <para>
    /// A null <c>teamId</c> UNASSIGNS the user, and that is a meaningful state rather than a missing
    /// value: an analyst with no team falls back to notifying every active RiskManager of the tenant
    /// when they pre-approve. Clearing the assignment is therefore a supported operation, not an
    /// error, which is why the body's field is nullable and its absence is not rejected.
    /// </para>
    /// </remarks>
    [HttpPut("users/{userId:guid}/team")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetUserTeam(
        Guid userId,
        [FromBody] SetUserTeamRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = ExtractTenantId();

        var userError = await EnsureUserInTenantAsync(userId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (userError is not null) return userError;

        var teamId = request?.TeamId;

        // A named team must exist within THIS tenant. GetByIdAsync carries the tenant predicate, so
        // an id belonging to another tenant simply does not resolve — an Admin cannot file one of
        // their users under someone else's team by guessing a GUID.
        if (teamId is Guid target)
        {
            var team = await _teamRepository
                .GetByIdAsync(target, tenantId, cancellationToken)
                .ConfigureAwait(false);

            if (team is null)
            {
                return Problem(
                    statusCode: 404,
                    title: "Not Found",
                    detail: "No se encontró el equipo.");
            }
        }

        await _userRepository
            .UpdateTeamAsync(userId, teamId, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Team assignment updated for user {UserId}, TenantId: {TenantId}, TeamId: {TeamId}",
            userId,
            tenantId,
            teamId);

        return NoContent();
    }

    /// <summary>
    /// 404s unless the target user exists AND belongs to the caller's tenant. Tenant scope is
    /// checked here rather than in the repository because <c>GetByUserIdAsync</c> is keyed by a
    /// globally unique id: without this, an Admin could read or rewrite the approval limits of a
    /// user in another tenant by guessing a GUID. Deliberately 404 and not 403 — an Admin has no
    /// business learning whether a given id exists elsewhere.
    /// </summary>
    private async Task<IActionResult?> EnsureUserInTenantAsync(
        Guid userId, string tenantId, CancellationToken ct)
    {
        var target = await _userRepository.GetByUserIdAsync(userId, ct).ConfigureAwait(false);

        if (target is null || !string.Equals(target.TenantId, tenantId, StringComparison.Ordinal))
        {
            return Problem(
                statusCode: 404,
                title: "Not Found",
                detail: "No se encontró el usuario.");
        }

        return null;
    }

    private static UserApprovalLimitResponse MapLimit(UserApprovalLimit limit) => new()
    {
        Currency   = limit.Currency,
        CanApprove = limit.CanApprove,
        MaxAmount  = limit.MaxAmount,
        UpdatedAt  = limit.UpdatedAt,
    };

    /// <summary>
    /// Creates a user invitation and sends the registration email.
    /// Requires Admin role.
    /// </summary>
    [HttpPost("users/invite")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(InviteUserResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> InviteUser(
        [FromBody] InviteUserSettingsRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
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

        if (!ValidRoles.Contains(request.Role))
        {
            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: $"El rol no es válido. Debe ser uno de: {string.Join(", ", ValidRoles)}");
        }

        var userId = ExtractUserId();
        var tenantId = ExtractTenantId();

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var requestId = HttpContext.TraceIdentifier;

        try
        {
            var invitation = await _invitationService
                .InviteUserAsync(
                    tenantId,
                    userId,
                    request,
                    ipAddress,
                    requestId,
                    cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Invitation sent to {EmailHash} for role {Role}, TenantId: {TenantId}",
                EmailHasher.Hash(request.Email),
                request.Role,
                tenantId);

            return StatusCode(StatusCodes.Status201Created, invitation);
        }
        // Before the base catch: the conflict is an InvalidOperationException too.
        catch (InvitationConflictException ex)
        {
            _logger.LogWarning(
                "Invitation refused for {EmailHash}: {ErrorCode}, TenantId: {TenantId}",
                EmailHasher.Hash(request.Email),
                ex.ErrorCode,
                tenantId);

            var problem = new ProblemDetails
            {
                Status   = StatusCodes.Status409Conflict,
                Title    = "Conflict",
                Detail   = ex.Message,
                Type     = $"https://serviciosfinancieros.api/errors/{ex.ErrorCode}",
                Instance = HttpContext.Request.Path
            };
            problem.Extensions["errorCode"] = ex.ErrorCode;
            return StatusCode(StatusCodes.Status409Conflict, problem);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to invite user: {EmailHash}, TenantId: {TenantId}",
                EmailHasher.Hash(request.Email),
                tenantId);

            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: ex.Message);
        }
    }

    /// <summary>
    /// Lists all invitations for the authenticated user's tenant, with derived status.
    /// Optional <c>?status=</c> filter (pending | accepted | expired | revoked); default returns all.
    /// Requires Admin role.
    /// </summary>
    [HttpGet("invitations")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(IReadOnlyList<InvitationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetInvitations(
        [FromQuery] string? status,
        CancellationToken cancellationToken)
    {
        var tenantId = ExtractTenantId();

        var invitations = await _invitationService
            .GetInvitationsAsync(tenantId, status, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Retrieved {Count} invitations for TenantId: {TenantId} (status filter: {Status})",
            invitations.Count,
            tenantId,
            status ?? "all");

        return Ok(invitations);
    }

    /// <summary>
    /// Resends a pending invitation: rotates its token, extends expiry, and re-sends the email,
    /// keeping the same InvitationId. Rejected (400) if already accepted or revoked. Requires Admin role.
    /// </summary>
    [HttpPost("invitations/{invitationId:guid}/resend")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResendInvitation(
        Guid invitationId,
        CancellationToken cancellationToken)
    {
        var userId = ExtractUserId();
        var tenantId = ExtractTenantId();
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var requestId = HttpContext.TraceIdentifier;

        var result = await _invitationService
            .ResendInvitationAsync(tenantId, invitationId, userId, ipAddress, requestId, cancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            InvitationActionResult.Success => NoContent(),
            InvitationActionResult.NotFound => Problem(
                statusCode: 404,
                title: "Not Found",
                detail: "No se encontró la invitación."),
            InvitationActionResult.InvalidState => Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: "No se puede reenviar una invitación que ya fue aceptada o revocada."),
            _ => Problem(statusCode: 500, title: "Internal Server Error", detail: "Unexpected result.")
        };
    }

    /// <summary>
    /// Revokes a pending invitation (sets RevokedAt/RevokedByUserId). Idempotent if already revoked.
    /// Rejected (400) if already accepted. Requires Admin role.
    /// </summary>
    [HttpDelete("invitations/{invitationId:guid}")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeInvitation(
        Guid invitationId,
        CancellationToken cancellationToken)
    {
        var userId = ExtractUserId();
        var tenantId = ExtractTenantId();
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var requestId = HttpContext.TraceIdentifier;

        var result = await _invitationService
            .RevokeInvitationAsync(tenantId, invitationId, userId, ipAddress, requestId, cancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            InvitationActionResult.Success => NoContent(),
            InvitationActionResult.NotFound => Problem(
                statusCode: 404,
                title: "Not Found",
                detail: "No se encontró la invitación."),
            InvitationActionResult.InvalidState => Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: "No se puede revocar una invitación que ya fue aceptada."),
            _ => Problem(statusCode: 500, title: "Internal Server Error", detail: "Unexpected result.")
        };
    }

    /// <summary>
    /// Returns the full scoring configuration for the tenant, merging DB overrides with defaults, plus the
    /// persona humana parameters in <c>personaHumana</c> (value, default, override flag, kind, bounds, unit, group).
    /// Requires Admin, RiskManager or Auditor role.
    /// </summary>
    [HttpGet("scoring-config")]
    [Authorize(Roles = Roles.TenantConfigReaders)]
    [ProducesResponseType(typeof(ScoringConfigDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetScoringConfig(CancellationToken cancellationToken)
    {
        var tenantId = ExtractTenantId();

        var config = await _settingsService
            .GetScoringConfigAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        return Ok(config);
    }

    /// <summary>
    /// Updates one or more scoring configuration parameters for the tenant. A pf_ item may carry
    /// <c>reset: true</c> instead of a value to go back to the code default.
    /// Requires Admin role.
    /// </summary>
    [HttpPut("scoring-config")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(ScoringConfigDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateScoringConfig(
        [FromBody] UpdateScoringConfigRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Items is null || request.Items.Count == 0)
        {
            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: "Se requiere al menos un elemento de configuración.");
        }

        var userId = ExtractUserId();
        var tenantId = ExtractTenantId();
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var requestId = HttpContext.TraceIdentifier;

        try
        {
            var result = await _settingsService
                .UpdateScoringConfigAsync(userId, tenantId, request, ipAddress, requestId, cancellationToken)
                .ConfigureAwait(false);

            if (result is null)
            {
                return Problem(
                    statusCode: 400,
                    title: "Bad Request",
                    detail: "Una o más claves de configuración no se reconocen.");
            }

            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex,
                "Scoring config validation failed for TenantId: {TenantId}",
                tenantId);

            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: ex.Message);
        }
    }

    // ── Sector profile endpoints ────────────────────────────────────────────────

    /// <summary>
    /// Returns all known sectors with an indicator of whether the tenant has threshold overrides.
    /// Requires Admin, RiskManager or Auditor role.
    /// </summary>
    [HttpGet("scoring-config/sectors")]
    [Authorize(Roles = Roles.TenantConfigReaders)]
    [ProducesResponseType(typeof(IReadOnlyList<SectorListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSectors(CancellationToken cancellationToken)
    {
        var tenantId = ExtractTenantId();

        var sectors = await _settingsService
            .GetSectorsAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        return Ok(sectors);
    }

    /// <summary>
    /// Returns the effective threshold profile for a sector, merging base + sector overrides + defaults.
    /// Requires Admin, RiskManager or Auditor role.
    /// </summary>
    [HttpGet("scoring-config/sectors/{sector}")]
    [Authorize(Roles = Roles.TenantConfigReaders)]
    [ProducesResponseType(typeof(SectorProfileDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSectorProfile(
        string sector,
        CancellationToken cancellationToken)
    {
        var tenantId = ExtractTenantId();

        var profile = await _settingsService
            .GetSectorProfileAsync(tenantId, sector, cancellationToken)
            .ConfigureAwait(false);

        return Ok(profile);
    }

    /// <summary>
    /// Creates or updates threshold overrides for a non-General sector.
    /// Requires Admin role.
    /// </summary>
    [HttpPut("scoring-config/sectors/{sector}")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(SectorProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateSectorProfile(
        string sector,
        [FromBody] UpdateSectorProfileRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Thresholds is null || request.Thresholds.Count == 0)
        {
            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: "Se requiere al menos un umbral.");
        }

        var userId = ExtractUserId();
        var tenantId = ExtractTenantId();
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var requestId = HttpContext.TraceIdentifier;

        try
        {
            var result = await _settingsService
                .UpdateSectorProfileAsync(userId, tenantId, sector, request, ipAddress, requestId, cancellationToken)
                .ConfigureAwait(false);

            if (result is null)
            {
                return Problem(
                    statusCode: 400,
                    title: "Bad Request",
                    detail: "No se puede actualizar el sector 'General' o una o más claves de umbral no se reconocen.");
            }

            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex,
                "Sector profile validation failed for sector {Sector}, TenantId: {TenantId}",
                sector, tenantId);

            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: ex.Message);
        }
    }

    // ── Tenant profile endpoints ────────────────────────────────────────────────

    /// <summary>
    /// Returns the tenant profile in force: the entity type (<c>sgr</c> / <c>non_sgr</c>) and the
    /// optional integrations it entitles the tenant to use.
    /// Requires Admin, RiskManager, Auditor or SuperAdmin role.
    /// </summary>
    /// <remarks>
    /// Distinct from <c>GET profile</c>, which is the authenticated USER's own profile. This one is
    /// tenant-level.
    /// <para>
    /// Reading is deliberately broader than writing, which is SuperAdmin alone: a RiskManager and an
    /// Auditor must be able to SEE which integrations the tenant is entitled to, because that
    /// explains the enrichment an analysis carries. SuperAdmin is in the read set because it is the
    /// role that SETS the profile and has to be able to read back what it set.
    /// </para>
    /// <para>
    /// Fails open: the underlying resolver never throws, so a settings read that fails answers with
    /// the SGR preset and <c>source: "ReadFailed"</c> rather than an error. A client presenting this
    /// as the tenant's settled configuration should treat that value as "unknown", not as a fact.
    /// </para>
    /// </remarks>
    [HttpGet("tenant-profile")]
    [Authorize(Roles = Roles.TenantConfigReadersWithOperator)]
    [ProducesResponseType(typeof(TenantProfileDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTenantProfile(CancellationToken cancellationToken)
    {
        var tenantId = ExtractTenantId();

        var profile = await _settingsService
            .GetTenantProfileAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        return Ok(profile);
    }

    /// <summary>
    /// Sets the tenant's entity type and returns the resulting profile.
    /// Requires SuperAdmin role.
    /// </summary>
    /// <remarks>
    /// This is OPERATOR configuration, not the client tenant's: the entity type decides which
    /// external vendors are called during enrichment, so moving it commits the tenant to per-query
    /// vendor billing. It therefore sits with the cross-tenant SuperAdmin, alongside the tenant plan
    /// (PUT /api/billing/config) — a tenant Admin still READS the profile on the GET above but can no
    /// longer set it.
    /// <para>
    /// Writes the <c>entity.type</c> row only. Any <c>integration.*.enabled</c> override the tenant
    /// already carries survives, and every key it has not overridden follows the new preset — so
    /// switching to <c>non_sgr</c> turns CASFOG and NOSIS off unless they were explicitly pinned on.
    /// </para>
    /// <para>
    /// Takes effect going FORWARD only. The profile gates whether a vendor is CALLED during
    /// enrichment; it is not read by the scoring engine, so no existing analysis is rescored and no
    /// snapshot already persisted on an entity is removed.
    /// </para>
    /// <para>
    /// Deliberately NOT fail-open, unlike the GET: reporting a profile change that did not happen is
    /// the worse outcome, so a failed write surfaces as a 500.
    /// </para>
    /// </remarks>
    [HttpPut("tenant-profile")]
    [Authorize(Roles = Roles.TenantConfigWriters)]
    [ProducesResponseType(typeof(TenantProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateTenantProfile(
        [FromBody] UpdateTenantProfileRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.EntityType))
        {
            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: "entityType es obligatorio.");
        }

        var userId = ExtractUserId();
        var tenantId = ExtractTenantId();
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var requestId = HttpContext.TraceIdentifier;

        try
        {
            var result = await _settingsService
                .UpdateTenantProfileAsync(userId, tenantId, request, ipAddress, requestId, cancellationToken)
                .ConfigureAwait(false);

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            // The stored value is operator input of unknown shape, so it is not echoed back or
            // logged — only the rejection is.
            _logger.LogWarning(
                "Tenant profile update rejected for TenantId: {TenantId}: unrecognised entity type",
                tenantId);

            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: ex.Message);
        }
    }

    // ── SGR Fondo de Riesgo endpoints ───────────────────────────────────────────

    /// <summary>
    /// Returns the tenant's SGR Fondo de Riesgo settings — the inputs of the portfolio dashboard's sgr
    /// section. Every field is null while it is not configured.
    /// Requires Admin role.
    /// </summary>
    [HttpGet("sgr")]
    [Authorize(Roles = Roles.Admin)]
    [ProducesResponseType(typeof(SgrSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSgrSettings(CancellationToken cancellationToken)
    {
        var tenantId = ExtractTenantId();

        var settings = await _sgrSettingsService
            .GetAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        return Ok(settings.ToDto());
    }

    /// <summary>
    /// Replaces the tenant's SGR Fondo de Riesgo settings and returns what is now stored.
    /// Requires Admin role.
    /// </summary>
    /// <remarks>
    /// <c>fondoRiesgoDisponible</c> (&gt; 0) and <c>fondoRiesgoFecha</c> are required;
    /// <c>tipoCambioUsd</c> (&gt; 0) and <c>tipoCambioFecha</c> go together, and both null removes the
    /// exchange rate. No date may be after today on the Argentine calendar. Audited as
    /// <c>Settings.UpdateSgrFundSettings</c> with the values before and after.
    /// </remarks>
    [HttpPut("sgr")]
    [Authorize(Roles = Roles.Admin)]
    [ProducesResponseType(typeof(SgrSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateSgrSettings(
        [FromBody] UpdateSgrSettingsRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: "Se requiere el cuerpo de la solicitud.");
        }

        var userId = ExtractUserId();
        var tenantId = ExtractTenantId();
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var requestId = HttpContext.TraceIdentifier;

        try
        {
            var result = await _sgrSettingsService
                .UpdateAsync(userId, tenantId, request, DateTime.UtcNow, ipAddress, requestId, cancellationToken)
                .ConfigureAwait(false);

            return Ok(result.ToDto());
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(
                "SGR settings update rejected for TenantId: {TenantId}: validation failed",
                tenantId);

            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: ex.Message);
        }
    }

    private Guid ExtractUserId()
    {
        var claim = User.FindFirst("userId")?.Value
                    ?? User.FindFirst("sub")?.Value;

        if (string.IsNullOrEmpty(claim) || !Guid.TryParse(claim, out var userId))
        {
            _logger.LogError(
                "UserId claim not found or invalid in JWT. Available claim types: {ClaimTypes}",
                string.Join(", ", User.Claims.Select(c => c.Type)));   // types only: values can be PII
            throw new UnauthorizedAccessException("UserId not found in JWT claims");
        }

        return userId;
    }

    private string ExtractTenantId()
    {
        return _tenantContextAccessor.TenantContext?.TenantId
               ?? User.FindFirst("tenantId")?.Value
               ?? throw new UnauthorizedAccessException("Tenant ID not found in claims");
    }
}
