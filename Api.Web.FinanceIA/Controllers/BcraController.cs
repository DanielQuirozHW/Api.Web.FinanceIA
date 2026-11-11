// Project : Api.Web.FinanceIA
// Created : 2026-05-08
// Modified: 2026-05-08

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// Proxies public BCRA API data through the BFF.
/// All endpoints require a valid internal JWT.
/// Results are served from IMemoryCache (TTL: 24 h for deudores, 1 h for macro/cambiario).
/// </summary>
[ApiController]
[Route("api/bcra")]
[Authorize(AuthenticationSchemes = "Internal")]
public sealed class BcraController : ControllerBase
{
    private readonly ITenantContextAccessor  _tenantContextAccessor;
    private readonly IBcraService            _bcraService;
    private readonly ILogger<BcraController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="BcraController"/>.
    /// </summary>
    public BcraController(
        ITenantContextAccessor  tenantContextAccessor,
        IBcraService            bcraService,
        ILogger<BcraController> logger)
    {
        _tenantContextAccessor = tenantContextAccessor;
        _bcraService           = bcraService;
        _logger                = logger;
    }

    // ── GET /api/bcra/deudores/{cuit} ─────────────────────────────────────────

    /// <summary>
    /// Returns the Central de Deudores record for the given CUIT.
    /// Result is cached per CUIT for 24 hours.
    /// </summary>
    /// <param name="cuit">Argentine CUIT (11 digits, no hyphens).</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 with deuda data, 404 when CUIT not found, 502 when BCRA is unavailable.</returns>
    [HttpGet("deudores/{cuit}")]
    [ProducesResponseType(200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 502)]
    public async Task<IActionResult> GetDeudoresAsync(
        string cuit,
        CancellationToken cancellationToken)
    {
        var tenantId = ResolveTenantId();
        if (tenantId is null)
            return TenantError();

        if (string.IsNullOrWhiteSpace(cuit) || cuit.Length != 11 || !cuit.All(char.IsDigit))
        {
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "El CUIT debe tener exactamente 11 dígitos sin guiones.");
        }

        var data = await _bcraService.GetDeudaAsync(cuit, cancellationToken).ConfigureAwait(false);

        if (data.Estado == BcraDeudaConsultaEstado.LookupFailed)
        {
            _logger.LogWarning(
                "BCRA deudores unavailable for CUIT {Cuit} tenant {TenantId}",
                cuit, tenantId);
            return Problem(statusCode: 502, title: "Bad Gateway",
                detail: "El servicio BCRA Central de Deudores no está disponible temporalmente.");
        }

        _logger.LogInformation(
            "BCRA deudores retrieved for CUIT {Cuit} tenant {TenantId} " +
            "situacionMaxima={Situacion} serviceName=BCRA success=true",
            cuit, tenantId, data.SituacionMaxima);

        return Ok(data);
    }

    // ── GET /api/bcra/macro ───────────────────────────────────────────────────

    /// <summary>
    /// Returns the most recent macroeconomic (monetary) variables from the BCRA.
    /// Result is cached for 1 hour.
    /// </summary>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 with macro data, 502 when BCRA is unavailable.</returns>
    [HttpGet("macro")]
    [ProducesResponseType(200)]
    [ProducesResponseType(typeof(ProblemDetails), 502)]
    public async Task<IActionResult> GetMacroAsync(CancellationToken cancellationToken)
    {
        var tenantId = ResolveTenantId();
        if (tenantId is null)
            return TenantError();

        var data = await _bcraService.GetMacroAsync(cancellationToken).ConfigureAwait(false);

        if (data is null)
        {
            _logger.LogWarning(
                "BCRA macro unavailable for tenant {TenantId} serviceName=BCRA success=false",
                tenantId);
            return Problem(statusCode: 502, title: "Bad Gateway",
                detail: "Las estadísticas monetarias de BCRA no están disponibles temporalmente.");
        }

        _logger.LogInformation(
            "BCRA macro retrieved for tenant {TenantId} inflacion={Inflacion} tasa={Tasa} serviceName=BCRA success=true",
            tenantId, data.InflacionMensual, data.TasaReferencia);

        return Ok(data);
    }

    // ── GET /api/bcra/cambiario ───────────────────────────────────────────────

    /// <summary>
    /// Returns the most recent exchange-rate variables from the BCRA.
    /// Result is cached for 1 hour.
    /// </summary>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 with cambiario data, 502 when BCRA is unavailable.</returns>
    [HttpGet("cambiario")]
    [ProducesResponseType(200)]
    [ProducesResponseType(typeof(ProblemDetails), 502)]
    public async Task<IActionResult> GetCambiarioAsync(CancellationToken cancellationToken)
    {
        var tenantId = ResolveTenantId();
        if (tenantId is null)
            return TenantError();

        var data = await _bcraService.GetCambiarioAsync(cancellationToken).ConfigureAwait(false);

        if (data is null)
        {
            _logger.LogWarning(
                "BCRA cambiario unavailable for tenant {TenantId} serviceName=BCRA success=false",
                tenantId);
            return Problem(statusCode: 502, title: "Bad Gateway",
                detail: "Las estadísticas cambiarias de BCRA no están disponibles temporalmente.");
        }

        _logger.LogInformation(
            "BCRA cambiario retrieved for tenant {TenantId} tipoCambio={TipoCambio} serviceName=BCRA success=true",
            tenantId, data.TipoCambioOficial);

        return Ok(data);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private string? ResolveTenantId()
    {
        var ctx = _tenantContextAccessor.TenantContext;
        if (ctx is null)
        {
            _logger.LogWarning("TenantContext not available in BcraController");
        }
        return ctx?.TenantId;
    }

    private IActionResult TenantError() =>
        Problem(statusCode: 500, title: "Internal Server Error",
            detail: "Tenant context not resolved.");
}
