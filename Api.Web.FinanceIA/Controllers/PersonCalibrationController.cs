// Project : Api.Web.FinanceIA
// Created : 2026-10-30

using System.Security.Cryptography;
using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// The persona humana calibration board (M10 pilot): the read the SGR's risk leads take into the 30-day /
/// 20-analysis review with the client — approvals against the suggestion or in red, rejections in green,
/// filters that return SinDatos, documents missing or stale, re-evaluations, and the pf_ parameters.
/// </summary>
/// <remarks>
/// <para>
/// READ-ONLY. The board writes nothing; the export writes one AuditLogs row, fire-and-forget, as a document
/// download does. Its own controller rather than an action on DashboardController so the role dashboards —
/// and the reflection test that pins that controller read-only — are untouched.
/// </para>
/// <para>
/// <see cref="Roles.CalibrationReaders"/> — Admin and RiskManager. Analyst, Auditor and SuperAdmin get 403.
/// </para>
/// <para>
/// Query: <c>from</c> and <c>to</c> are Argentine dates (<c>yyyy-MM-dd</c>, both inclusive; default: the last
/// 30 days ending today in Argentina; at most 366 days), <c>teamId</c> narrows to the analysts CURRENTLY in that
/// team (<c>teamAttribution: "current_membership"</c>). A malformed value is 400 <c>validation_error</c>; a
/// teamId that is not a team of this tenant is 404 <c>team_not_found</c> — an empty board would read as "this
/// team analysed nothing" when the id is simply wrong.
/// </para>
/// </remarks>
[ApiController]
[Route("api/dashboard/person-calibration")]
[Route("api/bff/dashboard/person-calibration")]
[Authorize(AuthenticationSchemes = "Internal")]
public sealed class PersonCalibrationController : ControllerBase
{
    /// <summary>The AuditLogs action of a CSV export.</summary>
    internal const string ExportAuditAction = "Dashboard.PersonCalibrationExported";

    /// <summary>The AuditLogs entity type of a CSV export.</summary>
    internal const string ExportAuditEntityType = "PersonCalibration";

    private static readonly JsonSerializerOptions AuditJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ITenantContextAccessor               _tenantContextAccessor;
    private readonly IUserRepository                      _userRepository;
    private readonly ITeamRepository                      _teamRepository;
    private readonly IPersonCalibrationService            _calibrationService;
    private readonly IAuditService                        _auditService;
    private readonly ILogger<PersonCalibrationController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="PersonCalibrationController"/>.
    /// </summary>
    public PersonCalibrationController(
        ITenantContextAccessor               tenantContextAccessor,
        IUserRepository                      userRepository,
        ITeamRepository                      teamRepository,
        IPersonCalibrationService            calibrationService,
        IAuditService                        auditService,
        ILogger<PersonCalibrationController> logger)
    {
        _tenantContextAccessor = tenantContextAccessor;
        _userRepository        = userRepository;
        _teamRepository        = teamRepository;
        _calibrationService    = calibrationService;
        _auditService          = auditService;
        _logger                = logger;
    }

    // ── GET /api/dashboard/person-calibration ─────────────────────────────────────

    /// <summary>The calibration board over the persona humana analyses created in the period.</summary>
    /// <param name="from">First Argentine day, <c>yyyy-MM-dd</c>, inclusive.</param>
    /// <param name="to">Last Argentine day, <c>yyyy-MM-dd</c>, inclusive.</param>
    /// <param name="teamId">Optional team: the analysts currently in it.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="PersonCalibrationResponse"/>; 400 validation_error; 404 team_not_found.</returns>
    [HttpGet]
    [Authorize(Roles = Roles.CalibrationReaders)]
    [ProducesResponseType(typeof(PersonCalibrationResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetAsync(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? teamId,
        CancellationToken   cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var nowUtc = DateTime.UtcNow;
        var (period, team, invalid) = await ResolveQueryAsync(tenantId, from, to, teamId, nowUtc, cancellationToken)
            .ConfigureAwait(false);
        if (invalid is not null) return invalid;

        var response = await _calibrationService
            .GetBoardAsync(tenantId, period, team, nowUtc, cancellationToken)
            .ConfigureAwait(false);

        return Ok(response);
    }

    // ── GET /api/dashboard/person-calibration/export ──────────────────────────────

    /// <summary>
    /// The same population as a CSV for Excel in es-AR — UTF-8 with BOM, <c>;</c>, decimal comma, dd/MM/yyyy —
    /// one row per analysis. Audited as <c>Dashboard.PersonCalibrationExported</c> with the period, the team and
    /// the analysis ids only.
    /// </summary>
    /// <param name="from">First Argentine day, <c>yyyy-MM-dd</c>, inclusive.</param>
    /// <param name="to">Last Argentine day, <c>yyyy-MM-dd</c>, inclusive.</param>
    /// <param name="teamId">Optional team: the analysts currently in it.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 with text/csv; 400 validation_error; 401 when the caller has no user row; 404 team_not_found.</returns>
    [HttpGet("export")]
    [Authorize(Roles = Roles.CalibrationReaders)]
    [ProducesResponseType(typeof(FileResult), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> ExportAsync(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? teamId,
        CancellationToken   cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var (period, team, invalid) = await ResolveQueryAsync(tenantId, from, to, teamId, DateTime.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        if (invalid is not null) return invalid;

        var currentUser = await ResolveCurrentUserAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (currentUser is null) return Unauthorized();

        var file = await _calibrationService
            .ExportAsync(tenantId, period, team, cancellationToken)
            .ConfigureAwait(false);

        // ── Audit (fire-and-forget) ──────────────────────────────────────────────
        // AuditLogs, not ExportLogs: an ExportLogs row is one analysis' report (AnalysisId NOT NULL), and this
        // file is a board over many. Ids, the period and the team only — no name, CUIT or reason.
        var auditPayload = JsonSerializer.Serialize(new
        {
            format      = "CSV",
            from        = period.From.ToString(PersonCalibrationPeriod.DateFormat, System.Globalization.CultureInfo.InvariantCulture),
            to          = period.To.ToString(PersonCalibrationPeriod.DateFormat, System.Globalization.CultureInfo.InvariantCulture),
            teamId      = team,
            rowCount    = file.AnalysisIds.Count,
            analysisIds = file.AnalysisIds,
            fileSha256  = Convert.ToHexString(SHA256.HashData(file.Content)).ToLowerInvariant()
        }, AuditJsonOptions);

        _ = _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       currentUser.UserId,
            action:       ExportAuditAction,
            severity:     "Info",
            entityType:   ExportAuditEntityType,
            entityId:     null,
            ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:    HttpContext.TraceIdentifier,
            newValueJson: auditPayload,
            ct:           CancellationToken.None);

        return File(file.Content, "text/csv; charset=utf-8", file.FileName);
    }

    // ── Private helpers ───────────────────────────────────────────────────────────

    private (string tenantId, IActionResult? error) ResolveTenant()
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in PersonCalibrationController");
            return (string.Empty, Problem(
                statusCode: 500, title: "Internal Server Error",
                detail: "Tenant context not resolved."));
        }

        return (tenantContext.TenantId, null);
    }

    /// <summary>
    /// The period and the team, validated before anything is read: the dates by
    /// <see cref="PersonCalibrationPeriod.Resolve"/>, the team as a GUID of this tenant. Parsed here rather than
    /// bound as <c>Guid?</c>/<c>DateOnly?</c> so a malformed value gets the house es-AR 400, not the model
    /// binder's English one.
    /// </summary>
    private async Task<(PersonCalibrationPeriod Period, Guid? TeamId, IActionResult? Error)> ResolveQueryAsync(
        string tenantId, string? from, string? to, string? teamId, DateTime nowUtc, CancellationToken ct)
    {
        var (period, field, message) = PersonCalibrationPeriod.Resolve(from, to, nowUtc);
        if (period is null)
            return (default, null, ValidationProblem(field!, message!));

        if (string.IsNullOrWhiteSpace(teamId))
            return (period.Value, null, null);

        if (!Guid.TryParse(teamId.Trim(), out var team))
            return (default, null, ValidationProblem("teamId", "El equipo indicado no es un identificador válido."));

        var row = await _teamRepository.GetByIdAsync(team, tenantId, ct).ConfigureAwait(false);
        if (row is null)
        {
            _logger.LogInformation(
                "Person calibration refused for tenant {TenantId}: team {TeamId} not found.", tenantId, team);
            return (default, null, CodedProblem(404, "Not Found", "team_not_found",
                "El equipo indicado no existe en esta organización."));
        }

        return (period.Value, team, null);
    }

    /// <summary>
    /// Resolves the calling user from the JWT, as DashboardController does — with its tenant check, because
    /// GetByFirebaseUidAsync carries no tenant predicate. A row of another tenant is no user at all.
    /// </summary>
    private async Task<User?> ResolveCurrentUserAsync(string tenantId, CancellationToken ct)
    {
        var firebaseUid = User.FindFirst("uid")?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrWhiteSpace(firebaseUid))
            return null;

        var user = await _userRepository.GetByFirebaseUidAsync(firebaseUid, ct).ConfigureAwait(false);
        if (user is null)
            return null;

        if (!string.Equals(user.TenantId, tenantId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Person calibration caller {UserId} does not belong to tenant {TenantId}; refused.",
                user.UserId, tenantId);
            return null;
        }

        return user;
    }

    /// <summary>The 400 shape DocumentsController uses: errorCode validation_error and errors[{field, message}].</summary>
    private IActionResult ValidationProblem(string field, string message)
    {
        var pd = new ProblemDetails
        {
            Status   = 400,
            Title    = "Validation Error",
            Detail   = "Se produjeron uno o más errores de validación.",
            Type     = "https://serviciosfinancieros.api/errors/validation_error",
            Instance = HttpContext.Request.Path
        };
        pd.Extensions["errorCode"] = "validation_error";
        pd.Extensions["errors"]    = new[] { new { field, message } };
        return StatusCode(400, pd);
    }

    private ObjectResult CodedProblem(int status, string title, string errorCode, string detail)
    {
        var result = Problem(statusCode: status, title: title, detail: detail);
        ((ProblemDetails)result.Value!).Extensions["errorCode"] = errorCode;
        return result;
    }
}
