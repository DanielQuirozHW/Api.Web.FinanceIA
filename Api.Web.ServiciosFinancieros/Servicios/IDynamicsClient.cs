// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-21
// Modified: 2026-04-21

using Api.Web.ServiciosFinancieros.Models;

namespace Api.Web.ServiciosFinancieros.Servicios;

/// <summary>
/// HTTP client for Dynamics 365 OData v9.2 API.
/// Automatically handles authentication, error mapping,
/// and pagination for multi-record queries.
/// </summary>
public interface IDynamicsClient
{
    /// <summary>
    /// Sends a GET request and deserializes the response to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The expected response type.</typeparam>
    /// <param name="entitySetName">OData entity set name (e.g. <c>accounts</c>).</param>
    /// <param name="oDataQuery">Optional OData query string (e.g. <c>$select=name&amp;$filter=...</c>).</param>
    /// <param name="tenantContext">The resolved tenant context. Must not be null.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>Deserialized response, or <c>null</c> when the response body is empty.</returns>
    Task<T?> GetAsync<T>(
        string entitySetName,
        string? oDataQuery,
        TenantContext tenantContext,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sends a POST request with <paramref name="body"/> and deserializes the response to <typeparamref name="TResponse"/>.
    /// </summary>
    /// <typeparam name="TRequest">The request body type.</typeparam>
    /// <typeparam name="TResponse">The expected response type.</typeparam>
    /// <param name="entitySetName">OData entity set name.</param>
    /// <param name="body">The request body. Must not be null.</param>
    /// <param name="tenantContext">The resolved tenant context. Must not be null.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>Deserialized response, or <c>null</c> when the response body is empty.</returns>
    Task<TResponse?> PostAsync<TRequest, TResponse>(
        string entitySetName,
        TRequest body,
        TenantContext tenantContext,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sends a PATCH request to update an existing record.
    /// </summary>
    /// <typeparam name="T">The type of the patch body.</typeparam>
    /// <param name="entitySetName">OData entity set name.</param>
    /// <param name="recordId">The unique identifier of the record to update. Must not be <see cref="Guid.Empty"/>.</param>
    /// <param name="body">The fields to update. Must not be null.</param>
    /// <param name="tenantContext">The resolved tenant context. Must not be null.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    Task PatchAsync<T>(
        string entitySetName,
        Guid recordId,
        T body,
        TenantContext tenantContext,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sends a DELETE request to remove an existing record.
    /// </summary>
    /// <param name="entitySetName">OData entity set name.</param>
    /// <param name="recordId">The unique identifier of the record to delete. Must not be <see cref="Guid.Empty"/>.</param>
    /// <param name="tenantContext">The resolved tenant context. Must not be null.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    Task DeleteAsync(
        string entitySetName,
        Guid recordId,
        TenantContext tenantContext,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sends a GET request and automatically follows OData <c>@odata.nextLink</c> pagination,
    /// returning all records as a flat list.
    /// </summary>
    /// <typeparam name="T">The element type in the OData value array.</typeparam>
    /// <param name="entitySetName">OData entity set name.</param>
    /// <param name="oDataQuery">Optional OData query string.</param>
    /// <param name="tenantContext">The resolved tenant context. Must not be null.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>All records across all pages.</returns>
    Task<List<T>> GetMultipleAsync<T>(
        string entitySetName,
        string? oDataQuery,
        TenantContext tenantContext,
        CancellationToken cancellationToken);

    /// <summary>
    /// Executes a FetchXML query against Dynamics 365.
    /// Automatically handles pagination using paging cookies.
    /// FetchXML must be URL-encoded before passing to this method.
    /// </summary>
    /// <typeparam name="T">The element type in the result collection.</typeparam>
    /// <param name="entitySetName">OData entity set name to query (e.g. <c>contacts</c>).</param>
    /// <param name="fetchXml">The FetchXML query string. Must not be null or whitespace.</param>
    /// <param name="tenantContext">The resolved tenant context. Must not be null.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>All records across all FetchXML pages.</returns>
    Task<List<T>> ExecuteFetchXmlAsync<T>(
        string entitySetName,
        string fetchXml,
        TenantContext tenantContext,
        CancellationToken cancellationToken);

    /// <summary>
    /// Executes a FetchXML query and returns a SINGLE page of results.
    /// Unlike <see cref="ExecuteFetchXmlAsync{T}"/>, this method does NOT automatically paginate.
    /// Returns the data for the requested page plus a paging cookie for the next page.
    /// Use this for frontend-controlled pagination (tables, lookups, etc.).
    /// </summary>
    /// <typeparam name="T">The element type in the result collection.</typeparam>
    /// <param name="entitySetName">Dynamics entity set name (e.g., 'contacts').</param>
    /// <param name="fetchXml">FetchXML query string (must include count attribute).</param>
    /// <param name="pageNumber">Page number to retrieve (1-based index).</param>
    /// <param name="pagingCookie">Paging cookie from previous response (null for first page).</param>
    /// <param name="tenantContext">Tenant configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="DynamicsFetchXmlPagedResult{T}"/> containing the page data and navigation state.
    /// </returns>
    Task<DynamicsFetchXmlPagedResult<T>> ExecuteFetchXmlPageAsync<T>(
        string entitySetName,
        string fetchXml,
        int pageNumber,
        string? pagingCookie,
        TenantContext tenantContext,
        CancellationToken cancellationToken);
}
