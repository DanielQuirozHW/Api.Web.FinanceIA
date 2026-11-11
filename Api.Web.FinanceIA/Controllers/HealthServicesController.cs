// Project : Api.Web.FinanceIA
// Created : 2026-06-07

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Health;
using Api.Web.FinanceIA.Servicios;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Web.FinanceIA.Controllers;

[ApiController]
[Route("api/health")]
[Authorize(AuthenticationSchemes = "Internal")]
public sealed class HealthServicesController : ControllerBase
{
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly IServiceStatusService _serviceStatusService;
    private readonly ILogger<HealthServicesController> _logger;

    public HealthServicesController(
        ITenantContextAccessor tenantContextAccessor,
        IServiceStatusService serviceStatusService,
        ILogger<HealthServicesController> logger)
    {
        _tenantContextAccessor = tenantContextAccessor;
        _serviceStatusService = serviceStatusService;
        _logger = logger;
    }

    /// <summary>
    /// Returns real-time service status: Claude platform, database connectivity,
    /// and the last AI call result for the current tenant.
    /// </summary>
    [HttpGet("services")]
    [ProducesResponseType(typeof(ServiceStatusResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetServicesAsync(CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        _logger.LogInformation(
            "Service status check requested by tenant {TenantId}", tenantId);

        var status = await _serviceStatusService
            .GetStatusAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        return Ok(status);
    }

    private (string tenantId, IActionResult? error) ResolveTenant()
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in HealthServicesController");
            return (string.Empty, Problem(
                statusCode: 500, title: "Internal Server Error",
                detail: "Tenant context not resolved."));
        }

        return (tenantContext.TenantId, null);
    }
}
