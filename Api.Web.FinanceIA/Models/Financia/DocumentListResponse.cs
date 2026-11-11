// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-05-04

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Paginated response for GET /api/documents.
/// </summary>
public sealed class DocumentListResponse
{
    /// <summary>The documents on the current page.</summary>
    public IReadOnlyList<DocumentResponse> Items { get; set; } = [];

    /// <summary>Current page number (1-based).</summary>
    public int Page { get; set; }

    /// <summary>Maximum number of items per page.</summary>
    public int PageSize { get; set; }

    /// <summary>Total number of documents matching the query (before pagination).</summary>
    public int Total { get; set; }
}
