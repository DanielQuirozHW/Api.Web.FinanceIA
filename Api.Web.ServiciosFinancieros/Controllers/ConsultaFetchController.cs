// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-24
// Modified: 2026-04-24

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Api.Web.ServiciosFinancieros.Clases;
using Api.Web.ServiciosFinancieros.Models;
using Api.Web.ServiciosFinancieros.Servicios;

namespace Api.Web.ServiciosFinancieros.Controllers;

/// <summary>
/// Exposes Dynamics 365 FetchXML query execution over HTTP.
/// Supports both automatic full-result pagination and
/// manual single-page pagination for frontend-controlled navigation.
/// </summary>
[ApiController]
[Route("api/consultafetch")]
[Authorize(AuthenticationSchemes = "SmartJwt")]
public sealed class ConsultaFetchController : ControllerBase
{
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly IDynamicsClient _dynamicsClient;
    private readonly ILogger<ConsultaFetchController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="ConsultaFetchController"/>.
    /// </summary>
    /// <param name="tenantContextAccessor">Accessor for the per-request tenant context.</param>
    /// <param name="dynamicsClient">Client for executing Dynamics 365 OData operations.</param>
    /// <param name="logger">Logger for structured diagnostics.</param>
    public ConsultaFetchController(
        ITenantContextAccessor tenantContextAccessor,
        IDynamicsClient dynamicsClient,
        ILogger<ConsultaFetchController> logger)
    {
        _tenantContextAccessor = tenantContextAccessor;
        _dynamicsClient = dynamicsClient;
        _logger = logger;
    }

    /// <summary>
    /// Executes a FetchXML query against Dynamics 365 and returns ALL records.
    /// Pagination is handled automatically by the service — the client receives
    /// the complete dataset in a single response.
    ///
    /// <para>Use this endpoint for:</para>
    /// <list type="bullet">
    ///   <item>Reports and data exports</item>
    ///   <item>Batch operations</item>
    ///   <item>Small to medium datasets (&lt; 10,000 records)</item>
    /// </list>
    ///
    /// <para>For large datasets or interactive UI elements (tables, lookups),
    /// use /ejecutar-paginado instead to control memory usage and response time.</para>
    ///
    /// <para>Example request:</para>
    /// <code>
    /// {
    ///   "entityName": "contacts",
    ///   "fetchXml": "&lt;fetch count='500'&gt;&lt;entity name='contact'&gt;
    ///   &lt;attribute name='fullname'/&gt;&lt;/entity&gt;&lt;/fetch&gt;"
    /// }
    /// </code>
    ///
    /// <para>The count attribute in FetchXML controls page size for internal pagination,
    /// but the response will contain all matching records.</para>
    /// </summary>
    /// <param name="request">The FetchXML query parameters.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>A flat list of all matching records.</returns>
    [HttpPost("ejecutar")]
    [ProducesResponseType(typeof(List<object>), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    [ProducesResponseType(typeof(ProblemDetails), 502)]
    [Authorize(AuthenticationSchemes = "SmartJwt")]
    public async Task<IActionResult> EjecutarFetchAsync(
        [FromBody] FetchXmlRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        if (ValidateRequestAndGetTenantContext(out var tenantContext) is { } problem)
        {
            return problem;
        }

        var results = await _dynamicsClient.ExecuteFetchXmlAsync<object>(
            request.EntityName,
            request.FetchXml,
            tenantContext!,
            cancellationToken);

        _logger.LogInformation(
            "FetchXML executed for tenant {TenantId}. Entity: {EntityName}, Total records: {Count}",
            tenantContext!.TenantId,
            request.EntityName,
            results.Count);

        return Ok(results);
    }

    /// <summary>
    /// Executes a FetchXML query and returns a SINGLE page of results.
    /// The client controls pagination by sending subsequent requests with
    /// incremented page numbers and the paging cookie from the previous response.
    ///
    /// <para>Use this endpoint for:</para>
    /// <list type="bullet">
    ///   <item>Interactive tables with page navigation</item>
    ///   <item>Lookup fields with many options (10k+ records)</item>
    ///   <item>Large datasets where loading all records would be slow</item>
    ///   <item>Progressive data loading (infinite scroll)</item>
    /// </list>
    ///
    /// <para>Example first page request:</para>
    /// <code>
    /// {
    ///   "entityName": "contacts",
    ///   "fetchXml": "&lt;fetch count='100'&gt;&lt;entity name='contact'&gt;
    ///   &lt;attribute name='fullname'/&gt;&lt;/entity&gt;&lt;/fetch&gt;",
    ///   "pageNumber": 1,
    ///   "pagingCookie": null
    /// }
    /// </code>
    ///
    /// <para>Example subsequent page request (use response from previous page):</para>
    /// <code>
    /// {
    ///   "entityName": "contacts",
    ///   "fetchXml": "&lt;fetch count='100'&gt;&lt;entity name='contact'&gt;
    ///   &lt;attribute name='fullname'/&gt;&lt;/entity&gt;&lt;/fetch&gt;",
    ///   "pageNumber": 2,
    ///   "pagingCookie": "%3ccookie%20page%3d%222%22%3e..."
    /// }
    /// </code>
    ///
    /// <para>IMPORTANT: The count attribute in FetchXML determines page size.
    /// Recommended values: 50-500 depending on entity complexity.</para>
    /// </summary>
    /// <param name="request">The FetchXML query parameters including page number and optional paging cookie.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>A <see cref="FetchXmlPagedResponse"/> with the page data and navigation state.</returns>
    [HttpPost("ejecutar-paginado")]
    [ProducesResponseType(typeof(FetchXmlPagedResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    [ProducesResponseType(typeof(ProblemDetails), 502)]
    [Authorize(AuthenticationSchemes = "SmartJwt")]
    public async Task<IActionResult> EjecutarFetchPaginadoAsync(
        [FromBody] FetchXmlPagedRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        if (ValidateRequestAndGetTenantContext(out var tenantContext) is { } problem)
        {
            return problem;
        }

        var result = await _dynamicsClient.ExecuteFetchXmlPageAsync<object>(
            request.EntityName,
            request.FetchXml,
            request.PageNumber,
            request.PagingCookie,
            tenantContext!,
            cancellationToken);

        var response = new FetchXmlPagedResponse
        {
            Data = result.Data,
            MoreRecords = result.MoreRecords,
            PagingCookie = result.PagingCookie,
            CurrentPage = result.CurrentPage,
            RecordCount = result.Data.Count
        };

        _logger.LogInformation(
            "FetchXML page {PageNumber} executed for tenant {TenantId}. Entity: {EntityName}, Records in page: {Count}, More records: {MoreRecords}",
            request.PageNumber,
            tenantContext!.TenantId,
            request.EntityName,
            response.RecordCount,
            response.MoreRecords);

        return Ok(response);
    }

    /// <summary>
    /// Validates that a <see cref="TenantContext"/> is available for the current request.
    /// </summary>
    /// <param name="tenantContext">
    /// When this method returns <c>null</c>, contains the resolved <see cref="TenantContext"/>.
    /// When this method returns a non-null result, this value is <c>null</c>.
    /// </param>
    /// <returns>
    /// <c>null</c> if validation passed; otherwise a <see cref="ProblemDetails"/> action result.
    /// </returns>
    private IActionResult? ValidateRequestAndGetTenantContext(out TenantContext? tenantContext)
    {
        tenantContext = _tenantContextAccessor.TenantContext;

        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in ConsultaFetchController");

            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        return null;
    }
}
