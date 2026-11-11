// Project : Api.Web.FinanceIA
// Created : 2026-08-25
// Modified: 2026-08-25

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The stored CASFOG snapshot of one entity, as returned by GET /api/entities/{id}/casfog.
///
/// <para>
/// Property names match <see cref="EntityResponse.Casfog"/> and
/// <see cref="EntityResponse.CasfogDataUpdatedAt"/> exactly, so the Portal reuses the parsing it
/// already has for the detail payload. This endpoint READS what is stored; it never consults
/// CASFOG. Re-consulting the external source is POST /api/entities/{id}/refresh-casfog.
/// </para>
/// </summary>
public sealed class EntityCasfogResponse
{
    /// <summary>The stored snapshot, or null when CASFOG has never been fetched for this entity.</summary>
    public CasfogData? Casfog { get; set; }

    /// <summary>UTC timestamp of the most recent CASFOG refresh. Null when never fetched.</summary>
    public DateTime? CasfogDataUpdatedAt { get; set; }
}
