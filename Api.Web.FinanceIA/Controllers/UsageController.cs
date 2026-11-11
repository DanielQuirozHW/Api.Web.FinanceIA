// Project : Api.Web.FinanceIA
// Created : 2026-06-07

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Time;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// Exposes per-tenant AI token usage aggregation.
/// All endpoints require an internal JWT (<c>Internal</c> scheme).
/// </summary>
[ApiController]
[Route("api/usage")]
[Authorize(AuthenticationSchemes = "Internal")]
public sealed class UsageController : ControllerBase
{
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly IUsageRepository _usageRepository;
    private readonly ILogger<UsageController> _logger;

    public UsageController(
        ITenantContextAccessor tenantContextAccessor,
        IUsageRepository usageRepository,
        ILogger<UsageController> logger)
    {
        _tenantContextAccessor = tenantContextAccessor;
        _usageRepository = usageRepository;
        _logger = logger;
    }

    /// <summary>
    /// Returns monthly token usage summary for the current tenant.
    /// Defaults to the current ARGENTINE year/month when parameters are omitted.
    /// </summary>
    /// <remarks>
    /// <paramref name="year"/> and <paramref name="month"/> name an ARGENTINE calendar month, the
    /// same period the billing quota meters. ArgentineTime converts it to the half-open UTC window
    /// the query compares against, so the report and the quota can never disagree about where a
    /// month begins.
    /// </remarks>
    [HttpGet("monthly")]
    [ProducesResponseType(typeof(UsageSummaryResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetMonthlyAsync(
        [FromQuery] int? year,
        [FromQuery] int? month,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        // Defaults come from the Argentine clock, not the UTC one: between 21:00 and midnight ART
        // on the last day of a month, UtcNow has already rolled into the next month and the report
        // would silently open on a period the user does not consider started yet.
        var today = ArgentineTime.ToLocal(DateTime.UtcNow);
        int resolvedYear = year ?? today.Year;
        int resolvedMonth = month ?? today.Month;

        if (resolvedMonth < 1 || resolvedMonth > 12)
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "El mes debe estar entre 1 y 12.");

        if (resolvedYear < 2024 || resolvedYear > 2100)
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "El año debe estar entre 2024 y 2100.");

        _logger.LogInformation(
            "Usage request for tenant {TenantId}, year={Year}, month={Month} (ART)",
            tenantId, resolvedYear, resolvedMonth);

        var period = ArgentineTime.PeriodFor(resolvedYear, resolvedMonth);

        var aggregate = await _usageRepository
            .GetUsageInPeriodAsync(tenantId, period.StartUtc, period.EndUtc, cancellationToken)
            .ConfigureAwait(false);

        var response = new UsageSummaryResponse
        {
            TotalInputTokens = aggregate.TotalInputTokens,
            TotalOutputTokens = aggregate.TotalOutputTokens,
            TotalCalls = aggregate.TotalCalls,
            Year = resolvedYear,
            Month = resolvedMonth,
            ByOperation = aggregate.ByOperation.Select(o => new UsageByOperation
            {
                Operation = o.Operation,
                InputTokens = o.InputTokens,
                OutputTokens = o.OutputTokens,
                Calls = o.Calls
            }).ToList(),
            ByDay = aggregate.ByDay.Select(d => new UsageByDay
            {
                Date = d.Day.ToString("yyyy-MM-dd"),
                InputTokens = d.InputTokens,
                OutputTokens = d.OutputTokens,
                Calls = d.Calls
            }).ToList()
        };

        return Ok(response);
    }

    private (string tenantId, IActionResult? error) ResolveTenant()
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in UsageController");
            return (string.Empty, Problem(
                statusCode: 500, title: "Internal Server Error",
                detail: "Tenant context not resolved."));
        }

        return (tenantContext.TenantId, null);
    }
}
