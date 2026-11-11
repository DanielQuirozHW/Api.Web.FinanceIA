// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-24
// Modified: 2026-04-24

namespace Api.Web.ServiciosFinancieros.Models;

/// <summary>
/// Result of a single-page FetchXML query execution.
/// Contains data for the current page and a paging cookie for the next request.
/// </summary>
/// <typeparam name="T">The entity type being queried.</typeparam>
public sealed record DynamicsFetchXmlPagedResult<T>
{
    /// <summary>Gets the records returned for the current page.</summary>
    public List<T> Data { get; init; } = new();

    /// <summary>
    /// Gets a value indicating whether more records exist beyond this page.
    /// </summary>
    public bool MoreRecords { get; init; }

    /// <summary>
    /// Gets the paging cookie to pass to the next page request,
    /// or <c>null</c> if no more pages are available.
    /// </summary>
    public string? PagingCookie { get; init; }

    /// <summary>Gets the current page number (1-based).</summary>
    public int CurrentPage { get; init; }
}
