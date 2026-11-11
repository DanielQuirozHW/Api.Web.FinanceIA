// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-24
// Modified: 2026-04-24

namespace Api.Web.ServiciosFinancieros.Models;

/// <summary>
/// Response model for paginated FetchXML queries.
/// If MoreRecords is true, include the PagingCookie in the next request
/// with PageNumber incremented by 1.
/// </summary>
public sealed record FetchXmlPagedResponse
{
    /// <summary>Gets the records returned for the current page.</summary>
    public List<object> Data { get; init; } = new();

    /// <summary>
    /// Gets a value indicating whether more records exist beyond the current page.
    /// </summary>
    public bool MoreRecords { get; init; }

    /// <summary>
    /// Gets the paging cookie to include in the next page request,
    /// or <c>null</c> when no further pages are available.
    /// </summary>
    public string? PagingCookie { get; init; }

    /// <summary>Gets the current page number (1-based).</summary>
    public int CurrentPage { get; init; }

    /// <summary>Gets the number of records returned in this page.</summary>
    public int RecordCount { get; init; }
}
