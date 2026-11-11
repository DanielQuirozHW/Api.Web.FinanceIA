// Project : Api.Web.FinanceIA
// Created : 2026-08-28

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Persistence operations for a PyME's fiadores (guarantors).
/// All reads are tenant-isolated. Exactly one fiador is Active per (TenantId, EntityId, CUIT) —
/// guarded in the database by the filtered unique index [UX_Fiadores_ActivePerEntityCuit], and
/// checked in code first (see <see cref="GetActiveByEntityCuitAsync"/>) so the caller returns a
/// clean 409 rather than letting the index throw. Removal is soft: <see cref="SetStatusAsync"/>
/// flips Status 'Active' -&gt; 'Removed' and nothing ever deletes a row, so the analyst's
/// selection history survives.
/// </summary>
public interface IFiadorRepository
{
    /// <summary>
    /// Retrieves the Active fiadores of one entity, tenant-isolated. 'Removed' rows are excluded —
    /// they are tombstones, not part of the PyME's current guarantor set. Ordered by Denominacion
    /// then CUIT so the list is stable across calls; Denominacion is nullable, and those rows sort
    /// first. Returns an empty list when the entity has no fiadores, or does not exist in this
    /// tenant — the caller checks entity existence separately.
    /// </summary>
    Task<IReadOnlyList<Fiador>> GetActiveByEntityAsync(
        Guid entityId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Retrieves the single Active fiador of an entity with the given CUIT, or null when none
    /// exists. Tenant-isolated. This is the pre-check behind the add path's 409: the CUIT must be
    /// in the same formatted, hyphenated form the column stores (see the controller's FormatCuit),
    /// because the comparison is a plain string equality and 30716327279 does not equal
    /// 30-71632727-9. A 'Removed' row does NOT match, so a previously removed fiador can be
    /// re-added — the same rule the filtered index enforces.
    /// </summary>
    Task<Fiador?> GetActiveByEntityCuitAsync(
        Guid entityId, string cuit, string tenantId, CancellationToken ct);

    /// <summary>
    /// Retrieves a fiador by its id, tenant-isolated, REGARDLESS of status. Returns null when the
    /// fiador does not exist or belongs to another tenant. Status-agnostic on purpose: the remove
    /// path needs to tell an already-'Removed' fiador (an idempotent 204) from a genuinely missing
    /// one (404), which an Active-only read could not do.
    /// </summary>
    Task<Fiador?> GetByIdAsync(Guid fiadorId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Appends a new fiador.
    /// </summary>
    Task CreateAsync(Fiador fiador, CancellationToken ct);

    /// <summary>
    /// Transitions a fiador's <see cref="Fiador.Status"/> from <paramref name="expectedStatus"/> to
    /// <paramref name="newStatus"/> in a single atomic conditional UPDATE, tenant-isolated. Writes
    /// only the Status column. Returns false when no row matches (missing, wrong tenant, or the
    /// status was already changed by a concurrent request) so the caller can 409 rather than
    /// silently overwrite. Mirrors <see cref="ICreditLineRepository.SetStatusAsync"/>.
    /// </summary>
    Task<bool> SetStatusAsync(
        Guid fiadorId, string tenantId, string expectedStatus, string newStatus, CancellationToken ct);

    /// <summary>
    /// Stores a fiador's own NOSIS snapshot in a single conditional UPDATE, tenant-isolated.
    /// Writes ONLY <see cref="Fiador.NosisDataJson"/> and <see cref="Fiador.NosisFetchedAt"/> —
    /// never Status, never any identity column — so a refresh cannot disturb the row it refreshes.
    /// Guarded on Status = 'Active': a fiador the analyst removed concurrently matches 0 rows and
    /// returns false, so the caller 409s rather than writing a snapshot onto a tombstone.
    /// Returns false equally when the fiador does not exist or belongs to another tenant.
    /// Mirrors <see cref="SetStatusAsync"/>.
    /// </summary>
    /// <param name="nosisDataJson">
    /// The serialized <c>NosisData</c>. Required and non-blank: this method exists to record a
    /// snapshot that was actually taken, and it must never be the path that blanks a good one.
    /// </param>
    /// <param name="fetchedAt">UTC instant the snapshot was taken.</param>
    Task<bool> SetNosisSnapshotAsync(
        Guid fiadorId, string tenantId, string nosisDataJson, DateTime fetchedAt, CancellationToken ct);

    /// <summary>
    /// Writes an analyst's correction (PATCH, DDL 074) in a single conditional UPDATE, tenant-isolated:
    /// <see cref="Fiador.Denominacion"/>, <see cref="Fiador.Cargo"/>, <see cref="Fiador.PorcAccionista"/>
    /// and the <see cref="Fiador.UpdatedAt"/> / <see cref="Fiador.UpdatedByUserId"/> stamp — the values
    /// passed are the full new state of the three fields, not a delta. Never touches CUIT, Origen, Status
    /// or the NOSIS snapshot. Guarded on Status = 'Active', so a fiador removed concurrently matches 0 rows
    /// and returns false for the caller to 409. Returns false equally when the fiador does not exist or
    /// belongs to another tenant. Mirrors <see cref="SetStatusAsync"/>.
    /// </summary>
    Task<bool> UpdateDetailsAsync(
        Guid fiadorId, string tenantId, string? denominacion, string? cargo, decimal? porcAccionista,
        DateTime updatedAt, Guid updatedByUserId, CancellationToken ct);

    /// <summary>
    /// True when <paramref name="entityId"/> has an Active fiador linked to
    /// <paramref name="personLegajoId"/>, tenant-isolated. This is what lets a document be uploaded to
    /// a fiador's legajo through the PyME's entity. A 'Removed' fiador does not count.
    /// </summary>
    Task<bool> HasActiveFiadorWithLegajoAsync(
        Guid entityId, Guid personLegajoId, string tenantId, CancellationToken ct);

    /// <summary>
    /// True when ANY entity of the tenant has an Active fiador linked to
    /// <paramref name="personLegajoId"/>. Backs the legajo-scoped refreshes: a legajo that is no
    /// entity's and no active fiador's is not reachable through them. A 'Removed' fiador does not count.
    /// </summary>
    Task<bool> AnyActiveWithLegajoAsync(Guid personLegajoId, string tenantId, CancellationToken ct);
}
