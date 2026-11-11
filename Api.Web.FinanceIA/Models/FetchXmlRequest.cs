// Project : Api.Web.FinanceIA
// Created : 2026-04-24
// Modified: 2026-04-24

using System.ComponentModel.DataAnnotations;

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// Request model for automatic FetchXML query execution.
/// The service will paginate automatically and return all records.
/// </summary>
public sealed record FetchXmlRequest
{
    /// <summary>Gets the Dynamics 365 entity set name to query (e.g. <c>contacts</c>).</summary>
    [Required]
    public string EntityName { get; init; } = string.Empty;

    /// <summary>Gets the FetchXML query string to execute.</summary>
    [Required]
    public string FetchXml { get; init; } = string.Empty;
}
