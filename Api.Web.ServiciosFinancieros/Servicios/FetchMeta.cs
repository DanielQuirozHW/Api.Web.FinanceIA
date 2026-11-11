// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-27
// Modified: 2026-04-27

namespace Api.Web.ServiciosFinancieros.Servicios;

/// <summary>
/// Metadata for an entity defining which fields can be
/// selected, filtered, and sorted. Acts as a whitelist.
/// </summary>
public sealed record FetchMeta
{
    /// <summary>Gets the logical name of the entity (e.g. <c>contact</c>).</summary>
    public string EntityLogicalName { get; init; } = string.Empty;

    /// <summary>Gets the OData entity set name (e.g. <c>contacts</c>).</summary>
    public string EntitySetName { get; init; } = string.Empty;

    /// <summary>Gets the set of field names that clients are allowed to select.</summary>
    public HashSet<string> Selectable { get; init; } = new();

    /// <summary>Gets the set of field names that clients are allowed to filter on.</summary>
    public HashSet<string> Filterable { get; init; } = new();

    /// <summary>Gets the set of field names that clients are allowed to sort by.</summary>
    public HashSet<string> Sortable { get; init; } = new();

    /// <summary>Gets the default sort field used when the client provides no sort directive.</summary>
    public string DefaultSort { get; init; } = string.Empty;

    /// <summary>Gets the maximum page size allowed for this entity.</summary>
    public int MaxPageSize { get; init; } = 500;
}
