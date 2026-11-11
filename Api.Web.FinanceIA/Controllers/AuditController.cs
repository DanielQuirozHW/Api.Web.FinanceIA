// Project : Api.Web.FinanceIA
// Created : 2026-06-20

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// Read-only audit log API for Admins and Auditors.
/// All endpoints require an authenticated internal JWT session with the Admin or Auditor role.
/// TenantId is always sourced from the resolved TenantContext — never from client input.
/// </summary>
[Authorize(AuthenticationSchemes = "Internal")]
[ApiController]
[Route("api/bff/audit")]
public sealed class AuditController : ControllerBase
{
    private static readonly HashSet<string> ValidSeverities =
        ["Info", "Warning", "Error", "Critical"];

    private readonly IAuditLogQueryService _auditLogQueryService;
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly ILogger<AuditController> _logger;

    public AuditController(
        IAuditLogQueryService auditLogQueryService,
        ITenantContextAccessor tenantContextAccessor,
        ILogger<AuditController> logger)
    {
        _auditLogQueryService = auditLogQueryService;
        _tenantContextAccessor = tenantContextAccessor;
        _logger = logger;
    }

    /// <summary>
    /// Returns a paginated, filterable audit log for the authenticated tenant.
    /// </summary>
    [HttpGet("logs")]
    [Authorize(Roles = Roles.AuditViewers)]
    [ProducesResponseType(typeof(AuditLogListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetLogs(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] Guid? userId = null,
        [FromQuery] string? action = null,
        [FromQuery] string? severity = null,
        [FromQuery] DateTime? dateFrom = null,
        [FromQuery] DateTime? dateTo = null,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        // ── Input validation ───────────────────────────────────────────────────
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        if (severity is not null && !ValidSeverities.Contains(severity))
        {
            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: "El valor de severidad no es válido. Debe ser uno de: Info, Warning, Error, Critical.");
        }

        if (dateFrom.HasValue && dateTo.HasValue && dateTo.Value < dateFrom.Value)
        {
            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: "dateFrom debe ser anterior o igual a dateTo.");
        }

        if (search is not null)
        {
            search = search.Trim();
            if (search.Length > 200) search = search[..200];
            if (search.Length == 0) search = null;
        }

        // ── Tenant resolution ─────────────────────────────────────────────────
        var (tenantId, tenantError) = ResolveTenant();
        if (tenantError is not null) return tenantError;

        _logger.LogInformation(
            "Audit log requested: TenantId {TenantId}, page {Page}/{PageSize}",
            tenantId, page, pageSize);

        try
        {
            var filter = new AuditLogFilter
            {
                Page = page,
                PageSize = pageSize,
                UserId = userId,
                Action = action,
                Severity = severity,
                DateFrom = dateFrom,
                DateTo = dateTo,
                Search = search,
            };

            var result = await _auditLogQueryService
                .GetLogsAsync(tenantId, filter, cancellationToken)
                .ConfigureAwait(false);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error fetching audit logs for TenantId {TenantId}",
                tenantId);

            return Problem(statusCode: 500, title: "Internal Server Error",
                detail: "An error occurred while retrieving audit logs.");
        }
    }

    /// <summary>
    /// Returns distinct filter values (actions and severities) for the authenticated tenant.
    /// Used to populate frontend filter dropdowns.
    /// </summary>
    [HttpGet("filters")]
    [Authorize(Roles = Roles.AuditViewers)]
    [ProducesResponseType(typeof(AuditLogFiltersResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFilters(CancellationToken cancellationToken)
    {
        var (tenantId, tenantError) = ResolveTenant();
        if (tenantError is not null) return tenantError;

        try
        {
            var result = await _auditLogQueryService
                .GetFiltersAsync(tenantId, cancellationToken)
                .ConfigureAwait(false);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error fetching audit filters for TenantId {TenantId}",
                tenantId);

            return Problem(statusCode: 500, title: "Internal Server Error",
                detail: "An error occurred while retrieving audit filters.");
        }
    }

    private (string tenantId, IActionResult? error) ResolveTenant()
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in AuditController");
            return (string.Empty, Problem(
                statusCode: 500, title: "Internal Server Error",
                detail: "Tenant context not resolved."));
        }
        return (tenantContext.TenantId, null);
    }
}
