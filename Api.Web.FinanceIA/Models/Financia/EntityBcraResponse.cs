// Project : Api.Web.FinanceIA
// Created : 2026-08-25
// Modified: 2026-08-25

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The stored BCRA snapshot of one entity, as returned by GET /api/entities/{id}/bcra.
///
/// <para>
/// Property names match <see cref="EntityResponse.Bcra"/> and
/// <see cref="EntityResponse.BcraDataUpdatedAt"/> exactly, so the Portal reuses the parsing it
/// already has for the detail payload. This endpoint READS what is stored; it never consults BCRA.
/// Re-consulting the external source is POST /api/entities/{id}/refresh-bcra.
/// </para>
/// </summary>
public sealed class EntityBcraResponse
{
    /// <summary>The stored snapshot, or null when BCRA has never been fetched for this entity.</summary>
    public BcraData? Bcra { get; set; }

    /// <summary>UTC timestamp of the most recent BCRA refresh. Null when never fetched.</summary>
    public DateTime? BcraDataUpdatedAt { get; set; }
}
