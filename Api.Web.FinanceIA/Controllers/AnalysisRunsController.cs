// Project : Api.Web.FinanceIA
// Created : 2026-11-03

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// GET /api/analyses/runs — the tenant's balance analysis runs: running (queued or in flight), failed
/// within a window, and completed in the last 24 hours. What the Portal opens from an ANALYSIS_FAILED
/// notification (SourceType "AnalysisRun", SourceId = the document).
///
/// <para>
/// Its own controller rather than an action on AnalysisController, whose constructor every analysis
/// test builds. No route clash: AnalysisController's <c>{id:guid}</c> cannot match "runs".
/// </para>
/// </summary>
[ApiController]
[Route("api/analyses/runs")]
[Authorize(AuthenticationSchemes = "Internal")]
public sealed class AnalysisRunsController : ControllerBase
{
    /// <summary>Default and bounds of the <c>days</c> query parameter.</summary>
    internal const int DefaultDays = 7;
    internal const int MinDays     = 1;
    internal const int MaxDays     = 30;

    private const string ErrorTypeBase = "https://serviciosfinancieros.api/errors/";

    private readonly ITenantContextAccessor           _tenantContextAccessor;
    private readonly IAnalysisRunListService          _runListService;
    private readonly ILogger<AnalysisRunsController>  _logger;

    /// <summary>Initializes a new instance of <see cref="AnalysisRunsController"/>.</summary>
    public AnalysisRunsController(
        ITenantContextAccessor          tenantContextAccessor,
        IAnalysisRunListService         runListService,
        ILogger<AnalysisRunsController> logger)
    {
        _tenantContextAccessor = tenantContextAccessor;
        _runListService        = runListService;
        _logger                = logger;
    }

    /// <summary>
    /// Lists the tenant's balance analysis runs. Each list is newest first and capped at 50.
    /// </summary>
    /// <param name="days">Window of the failed list, in days: 1..30. Default 7.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <response code="200">The three lists.</response>
    /// <response code="400">days outside 1..30 (errorCode <c>invalid_days</c>).</response>
    [HttpGet]
    [Authorize(Roles = Roles.All)]
    [ProducesResponseType(typeof(AnalysisRunsResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetRunsAsync(
        [FromQuery] int   days              = DefaultDays,
        CancellationToken cancellationToken = default)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in AnalysisRunsController");
            return Problem(statusCode: 500, title: "Internal Server Error", detail: "Tenant context not resolved.");
        }

        if (days is < MinDays or > MaxDays)
        {
            var problem = new ProblemDetails
            {
                Status   = 400,
                Title    = "Bad Request",
                Detail   = $"El parámetro days debe estar entre {MinDays} y {MaxDays}.",
                Type     = ErrorTypeBase + "invalid_days",
                Instance = HttpContext.Request.Path
            };
            problem.Extensions["errorCode"] = "invalid_days";
            return BadRequest(problem);
        }

        var callerCanTrigger = AnalysisRunListService.CanTrigger(User.IsInRole);

        var response = await _runListService
            .GetRunsAsync(tenantContext.TenantId, days, callerCanTrigger, DateTime.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        return Ok(response);
    }
}
