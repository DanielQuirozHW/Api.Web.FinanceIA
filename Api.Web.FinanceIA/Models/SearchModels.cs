// Project : Api.Web.FinanceIA
// Created : 2026-04-27
// Modified: 2026-04-27

using System.ComponentModel.DataAnnotations;

namespace Api.Web.FinanceIA.Models;

// ──────────────────────────────────────────────────────────────────────────────
// Enums
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Supported filter operators for secure search requests.
/// </summary>
public enum FilterOp
{
    Eq,
    Neq,
    Lt,
    Lte,
    Gt,
    Gte,
    StartsWith,
    Contains,
    In,
    Between,
    IsNull,
    IsNotNull
}

/// <summary>
/// Join type for link-entity in FetchXML.
/// </summary>
public enum LinkType
{
    Inner,
    Outer
}

// ──────────────────────────────────────────────────────────────────────────────
// Core models
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Secure search request. Server builds FetchXML with automatic
/// security filters - client cannot bypass them.
/// </summary>
public sealed record SearchRequest
{
    /// <summary>Gets the logical name of the Dynamics 365 entity to query.</summary>
    [Required]
    public string Entity { get; init; } = string.Empty;

    /// <summary>Gets the list of fields to include in the result. When null, all selectable fields are returned.</summary>
    public string[]? Select { get; init; }

    /// <summary>Gets the list of filter conditions to apply.</summary>
    public List<SearchFilter>? Filters { get; init; }

    /// <summary>Gets the list of sort directives to apply.</summary>
    public List<SearchSort>? Sort { get; init; }

    /// <summary>Gets the list of join definitions (link-entities).</summary>
    public List<LinkEntity>? LinkEntities { get; init; }

    /// <summary>Gets the 1-based page number to retrieve.</summary>
    [Range(1, int.MaxValue)]
    public int Page { get; init; } = 1;

    /// <summary>Gets the maximum number of records per page. Clamped by MetaRegistry.MaxPageSize.</summary>
    [Range(1, 500)]
    public int PageSize { get; init; } = 50;
}

/// <summary>
/// Response from the secure search endpoint.
/// </summary>
public sealed record SearchResponse
{
    /// <summary>Gets the records for the current page.</summary>
    public List<object> Data { get; init; } = new();

    /// <summary>Gets a value indicating whether additional pages exist.</summary>
    public bool MoreRecords { get; init; }

    /// <summary>Gets the paging cookie to pass for the next page request, or <c>null</c> if no more pages.</summary>
    public string? PagingCookie { get; init; }

    /// <summary>Gets the current page number (1-based).</summary>
    public int CurrentPage { get; init; }

    /// <summary>Gets the number of records returned in this page.</summary>
    public int TotalInPage { get; init; }
}

// ──────────────────────────────────────────────────────────────────────────────
// Supporting models
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A single filter condition in a secure search request.
/// </summary>
public sealed record SearchFilter
{
    /// <summary>Gets the field name to filter on.</summary>
    [Required]
    public string Field { get; init; } = string.Empty;

    /// <summary>Gets the comparison operator.</summary>
    public FilterOp Op { get; init; }

    /// <summary>Gets the filter value. For <see cref="FilterOp.Between"/>, this is the lower bound.</summary>
    public object? Value { get; init; }

    /// <summary>Gets the upper bound value for <see cref="FilterOp.Between"/>.</summary>
    public object? Value2 { get; init; }
}

/// <summary>
/// A sort directive in a secure search request.
/// </summary>
public sealed record SearchSort
{
    /// <summary>Gets the field name to sort by.</summary>
    [Required]
    public string Field { get; init; } = string.Empty;

    /// <summary>Gets the sort direction. Must be <c>asc</c> or <c>desc</c>.</summary>
    [RegularExpression("^(asc|desc)$")]
    public string Dir { get; init; } = "asc";
}

/// <summary>
/// Represents a join (link-entity) in FetchXML.
/// Validated server-side against MetaRegistry.
/// </summary>
public sealed record LinkEntity
{
    /// <summary>Gets the logical name of the entity to join.</summary>
    [Required]
    public string EntityName { get; init; } = string.Empty;

    /// <summary>Gets the attribute on the linked entity to join from.</summary>
    [Required]
    public string FromAttribute { get; init; } = string.Empty;

    /// <summary>Gets the attribute on the primary entity to join to.</summary>
    [Required]
    public string ToAttribute { get; init; } = string.Empty;

    /// <summary>Gets the alias for the linked entity in the result set.</summary>
    [Required]
    public string Alias { get; init; } = string.Empty;

    /// <summary>Gets the list of fields to select from the linked entity.</summary>
    public string[]? Select { get; init; }

    /// <summary>Gets the filters to apply on the linked entity.</summary>
    public List<SearchFilter>? Filters { get; init; }

    /// <summary>Gets the join type (inner or outer).</summary>
    public LinkType Type { get; init; } = LinkType.Inner;
}
