// Project : Api.Web.FinanceIA
// Created : 2026-08-25
// Modified: 2026-08-25

using Api.Web.FinanceIA.Models.Nosis;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The stored NOSIS snapshot of one entity, as returned by GET /api/entities/{id}/nosis.
///
/// <para>
/// Property names match <see cref="EntityResponse.Nosis"/> and
/// <see cref="EntityResponse.NosisDataUpdatedAt"/> exactly, so the Portal reuses the parsing it
/// already has for the detail payload. This endpoint READS what is stored; it never consults
/// NOSIS — which matters more here than for its two siblings, because NOSIS is billed per query.
/// Re-consulting the external source is POST /api/entities/{id}/refresh-nosis.
/// </para>
///
/// <para>
/// <see cref="NosisData"/> carries PII, and since the analyst's primary variable set was mapped
/// that now includes natural-person data (documento, fecha de nacimiento, sexo, edad, domicilio)
/// rather than only entity identifiers. It is returned to the authenticated, tenant-scoped caller
/// exactly as the detail response already did, and it is never logged.
/// </para>
/// </summary>
public sealed class EntityNosisResponse
{
    /// <summary>The stored snapshot, or null when NOSIS has never been fetched for this entity.</summary>
    public NosisData? Nosis { get; set; }

    /// <summary>UTC timestamp of the most recent NOSIS refresh. Null when never fetched.</summary>
    public DateTime? NosisDataUpdatedAt { get; set; }

    /// <summary>
    /// The NOSIS Manager "Informe Individual" an analyst uploaded (DDL 087), parsed into company-level
    /// data, or null when none was imported. Unlike <see cref="Nosis"/> it carries no personal data.
    /// Display only. Imported with POST /api/entities/{id}/nosis/report, removed with DELETE.
    /// </summary>
    public NosisManagerReport? Report { get; set; }
}
