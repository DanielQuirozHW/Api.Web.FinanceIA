// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-24
// Modified: 2026-04-24

using System.ComponentModel.DataAnnotations;

namespace Api.Web.ServiciosFinancieros.Models;

/// <summary>
/// Request model for manual paginated FetchXML query execution.
/// Returns one page at a time. Include PagingCookie from previous response
/// to fetch subsequent pages.
/// </summary>
public sealed record FetchXmlPagedRequest
{
    /// <summary>Gets the Dynamics 365 entity set name to query (e.g. <c>contacts</c>).</summary>
    [Required]
    public string EntityName { get; init; } = string.Empty;

    /// <summary>Gets the FetchXML query string to execute. Must include a <c>count</c> attribute.</summary>
    [Required]
    public string FetchXml { get; init; } = string.Empty;

    /// <summary>Gets the page number to retrieve (1-based index).</summary>
    [Range(1, int.MaxValue)]
    public int PageNumber { get; init; } = 1;

    /// <summary>
    /// Gets the paging cookie returned by the previous page response,
    /// or <c>null</c> when requesting the first page.
    /// </summary>
    public string? PagingCookie { get; init; }
}
