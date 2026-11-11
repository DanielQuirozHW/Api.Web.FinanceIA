// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-08-25

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Paginated response for GET /api/entities.
/// </summary>
public sealed class EntityListResponse
{
    /// <summary>
    /// The entities on the current page, as the lightweight list projection.
    /// Carries only what the list view renders — see <see cref="EntityListItemResponse"/> for why
    /// this is not the full <see cref="EntityResponse"/>.
    /// </summary>
    public IReadOnlyList<EntityListItemResponse> Items { get; set; } = [];

    /// <summary>Current page number (1-based).</summary>
    public int Page { get; set; }

    /// <summary>Maximum number of items per page.</summary>
    public int PageSize { get; set; }

    /// <summary>Total number of entities in the tenant.</summary>
    public int Total { get; set; }
}
