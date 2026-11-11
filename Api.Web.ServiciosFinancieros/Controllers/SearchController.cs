// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-27
// Modified: 2026-04-27

using Api.Web.ServiciosFinancieros.Clases;
using Api.Web.ServiciosFinancieros.Models;
using Api.Web.ServiciosFinancieros.Servicios;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Web.ServiciosFinancieros.Controllers;

/// <summary>
/// Secure search endpoint with server-side FetchXML construction.
///
/// SECURITY: Client sends structured search parameters. Server builds
/// FetchXML with automatic row-level security filters based on user's
/// contactid/accountid from Firestore. Client CANNOT bypass these filters.
///
/// This is the recommended pattern for all data access.
///
/// Supports:
/// - Field selection (select)
/// - Filtering with multiple operators
/// - Sorting
/// - Joins via link-entities
/// - Pagination with paging cookies
/// </summary>
[ApiController]
[Route("api/search")]
[Authorize(AuthenticationSchemes = "SmartJwt")]
public sealed class SearchController : ControllerBase
{
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly IDynamicsClient _dynamicsClient;
    private readonly IUserContextResolver _userContextResolver;
    private readonly ILogger<SearchController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="SearchController"/>.
    /// </summary>
    public SearchController(
        ITenantContextAccessor tenantContextAccessor,
        IDynamicsClient dynamicsClient,
        IUserContextResolver userContextResolver,
        ILogger<SearchController> logger)
    {
        _tenantContextAccessor = tenantContextAccessor;
        _dynamicsClient = dynamicsClient;
        _userContextResolver = userContextResolver;
        _logger = logger;
    }

    /// <summary>
    /// Executes a secure, server-side search against Dynamics 365.
    ///
    /// Example request:
    /// <code>
    /// {
    ///   "entity": "contact",
    ///   "select": ["fullname", "emailaddress1"],
    ///   "filters": [
    ///     { "field": "address1_city", "op": "Eq", "value": "Buenos Aires" }
    ///   ],
    ///   "sort": [{ "field": "fullname", "dir": "asc" }],
    ///   "page": 1,
    ///   "pageSize": 50
    /// }
    /// </code>
    /// </summary>
    /// <param name="request">The structured search request.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>A paginated <see cref="SearchResponse"/>.</returns>
    [HttpPost]
    [ProducesResponseType(typeof(SearchResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> SearchAsync(
        [FromBody] SearchRequest request,
        CancellationToken cancellationToken)
    {
        // Step 1 — Validate model state
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        // Step 2 — Get TenantContext
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in SearchController");
            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        // Step 3 — Extract user ID from claims
        var uid = User.FindFirst("uid")?.Value
               ?? User.FindFirst("sub")?.Value;

        if (string.IsNullOrWhiteSpace(uid))
        {
            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: "User identifier claim not found.");
        }

        // Step 4 — Resolve user context from Firestore
        var userContext = await _userContextResolver.ResolveAsync(
            uid, tenantContext, cancellationToken).ConfigureAwait(false);

        if (userContext is null)
        {
            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: "User not found in Firestore.");
        }

        if (string.IsNullOrWhiteSpace(userContext.ContactId) &&
            string.IsNullOrWhiteSpace(userContext.AccountId) &&
            string.IsNullOrWhiteSpace(userContext.EmployeeId))
        {
            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: "User has no valid identity IDs.");
        }

        // Step 5 — Validate entity is registered
        if (!MetaRegistry.Entities.TryGetValue(request.Entity, out var meta))
        {
            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: $"Entity '{request.Entity}' is not supported.");
        }

        // Step 6 — Enforce security
        SecurityRegistry.Rules.TryGetValue(meta.EntityLogicalName, out var rule);

        var securityResult = SearchSecurityEnforcer.Enforce(
            request,
            userContext.AccountId,
            userContext.ContactId,
            userContext.EmployeeId,
            userContext.Email,
            rule,
            out var sanitizedRequest);

        if (securityResult is not null)
        {
            return securityResult;
        }

        // Step 7 — Build secure FetchXML
        var entityBlock = FetchXmlSafeBuilder.Build(sanitizedRequest, meta);

        var fullFetch =
            $"<fetch count='{sanitizedRequest.PageSize}' page='{sanitizedRequest.Page}'>" +
            entityBlock +
            "</fetch>";

        // Step 8 — Execute query
        var result = await _dynamicsClient.ExecuteFetchXmlPageAsync<object>(
            meta.EntitySetName,
            fullFetch,
            sanitizedRequest.Page,
            null,
            tenantContext,
            cancellationToken).ConfigureAwait(false);

        // Step 9 — Map to response
        var response = new SearchResponse
        {
            Data = result.Data,
            MoreRecords = result.MoreRecords,
            PagingCookie = result.PagingCookie,
            CurrentPage = result.CurrentPage,
            TotalInPage = result.Data.Count
        };

        // Step 10 — Log and return
        _logger.LogInformation(
            "Search executed for tenant {TenantId}. " +
            "Entity: {Entity}, Page: {Page}, Results: {Count}",
            tenantContext.TenantId,
            request.Entity,
            request.Page,
            response.TotalInPage);

        return Ok(response);
    }
}
