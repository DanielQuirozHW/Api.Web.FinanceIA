// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-05-04

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Persistence operations for <see cref="Entity"/> records
/// in the per-tenant business database.
/// </summary>
public interface IEntityRepository
{
    /// <summary>
    /// Returns the entity with the specified id belonging to the given tenant,
    /// or null if not found.
    /// </summary>
    Task<Entity?> GetByIdAsync(Guid entityId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns whether an entity with the specified id exists in the given tenant. The boolean an
    /// endpoint needs to decide 404-vs-proceed, and nothing else.
    /// <para>
    /// Deliberately not <see cref="GetByIdAsync"/>: that one is a full-row load, so an existence
    /// check answered with it made SQL Server read all five <c>nvarchar(max)</c> source snapshots
    /// off disk to produce a yes/no. A populated row does not fit in the 8060-byte limit — the LUFE
    /// blob alone is ~26KB on a representative PyME — so the snapshots live on off-row LOB pages and
    /// each check followed the in-row pointers to read those page chains. <c>AnyAsync</c> emits
    /// <c>SELECT CASE WHEN EXISTS (SELECT 1 FROM [Entities] WHERE …) …</c>, which names no column at
    /// all: there is no LOB pointer to follow, so none of the five is touched.
    /// </para>
    /// <para>
    /// Same <c>(EntityId, TenantId)</c> predicate as <see cref="GetByIdAsync"/>, so the tenant
    /// isolation is identical — an entity belonging to another tenant reports as absent, and the
    /// caller's 404 is unchanged.
    /// </para>
    /// <para>
    /// Use this ONLY where the loaded entity was never read for a field. Every caller that goes on
    /// to use one — the create/update paths, the <c>refresh-*</c> actions reading <c>CUIT</c>, the
    /// Lufe reads deserialising <c>LufeDataJson</c> — must keep <see cref="GetByIdAsync"/>.
    /// </para>
    /// </summary>
    Task<bool> ExistsByIdAsync(Guid entityId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns all entities belonging to the given tenant.
    /// </summary>
    Task<IReadOnlyList<Entity>> GetAllByTenantAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns the entity with the specified CUIT belonging to the given tenant,
    /// or null if not found.
    /// </summary>
    Task<Entity?> GetByCuitAsync(string cuit, string tenantId, CancellationToken ct);

    /// <summary>
    /// Persists a new entity record and returns the saved entity.
    /// </summary>
    Task<Entity> CreateAsync(Entity entity, CancellationToken ct);

    /// <summary>
    /// Persists changes to an existing entity record.
    /// </summary>
    Task<Entity> UpdateAsync(Entity entity, CancellationToken ct);

    /// <summary>
    /// Maps each of <paramref name="entityIds"/> to the business name and CUIT a list row displays,
    /// tenant-isolated. Ids with no entity in this tenant are absent from the map.
    /// <para>
    /// One set-based query projecting three columns. Callers that need only the display fields for a
    /// known, bounded set of ids must use this instead of <see cref="GetAllByTenantAsync"/>: that one
    /// reads every row of the tenant including all five <c>nvarchar(max)</c> source snapshots, which
    /// is a full-table blob scan to answer a question about ≤pageSize ids.
    /// </para>
    /// </summary>
    Task<IReadOnlyDictionary<Guid, EntityNameProjection>> GetNameLookupByIdsAsync(
        IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns the business name and CUIT of ONE entity, tenant-isolated, or null if there is no such
    /// entity in this tenant — which the caller turns into a 404.
    /// <para>
    /// The single-id form of <see cref="GetNameLookupByIdsAsync"/>, sharing its projection. It exists
    /// for GET /api/entities/{id}/label, which fills a breadcrumb: the Portal was calling GET
    /// /api/entities/{id} — <see cref="GetDetailByIdAsync"/>, two <c>nvarchar(max)</c> columns wide,
    /// FiscalDataJson and LufeDataJson, ~26KB of LUFE on a representative PyME — to read one string.
    /// This SELECT names three narrow columns and no blob at all.
    /// </para>
    /// <para>
    /// Deliberately not <see cref="GetByIdAsync"/> (a full-row load: all five snapshots) and not
    /// <see cref="GetNameLookupByIdsAsync"/> with a one-element list, which would emit an
    /// <c>IN (…)</c> and materialise a dictionary to answer a question about a primary key.
    /// </para>
    /// </summary>
    Task<EntityNameProjection?> GetNameByIdAsync(Guid entityId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns ONE PAGE of the tenant's entities as <see cref="EntityListProjection"/>, newest first,
    /// plus the total row count of the unpaged set.
    /// <para>
    /// Deliberately not <see cref="GetAllByTenantAsync"/> + in-memory <c>Skip/Take</c>: the ordering
    /// and the paging run in SQL (<c>ORDER BY … OFFSET … FETCH</c>) and the SELECT names only the
    /// columns the list renders, so the five <c>nvarchar(max)</c> source snapshots are never read off
    /// disk. <c>TotalCount</c> is a separate <c>COUNT(*)</c> over the same filter — cheap, and it
    /// touches no blob either.
    /// </para>
    /// <para>
    /// Ordering is <c>CreatedAt DESC, EntityId ASC</c>. The EntityId tie-break is load-bearing, not
    /// decoration: <c>CreatedAt</c> is a non-unique <c>datetime2</c> — two entities created in the
    /// same instant is entirely ordinary for a bulk import — and OFFSET/FETCH over a non-total order
    /// lets rows sharing a timestamp appear on two pages or on neither.
    /// </para>
    /// </summary>
    /// <param name="skip">Rows to skip — <c>(page - 1) * pageSize</c>, already clamped by the caller.</param>
    /// <param name="take">Rows to return — the clamped pageSize.</param>
    Task<(IReadOnlyList<EntityListProjection> Items, int TotalCount)> GetListPageByTenantAsync(
        string tenantId, int skip, int take, CancellationToken ct);

    /// <summary>
    /// Returns the entity behind GET /api/entities/{id} as <see cref="EntityDetailProjection"/>,
    /// tenant-isolated, or null if there is no such entity in this tenant.
    /// <para>
    /// Deliberately not <see cref="GetByIdAsync"/>: that one is a full-row load, so SQL Server reads
    /// all five <c>nvarchar(max)</c> source snapshots off disk. The detail view only renders FISCAL
    /// and LUFE on load — BCRA, CASFOG and NOSIS appear when the user opens their tabs — so this
    /// SELECT names those two blobs and omits the other three. Their <c>*DataUpdatedAt</c> columns
    /// ARE selected: plain <c>datetime2</c>, free to read, and the signal the client uses to render
    /// "last refreshed" without fetching the payload.
    /// </para>
    /// <para>
    /// <see cref="GetByIdAsync"/> stays for the write paths. Every <c>refresh-*</c> action and the
    /// update path mutate the entity and hand it to <see cref="UpdateAsync"/>, which marks every
    /// property modified — those need the whole row and must not be given a projection.
    /// </para>
    /// </summary>
    Task<EntityDetailProjection?> GetDetailByIdAsync(Guid entityId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns the STORED BCRA snapshot of one entity, tenant-isolated, or null if there is no such
    /// entity in this tenant. A read of three columns; it does not consult BCRA.
    /// <para>
    /// A non-null result whose <see cref="EntitySnapshotProjection.Json"/> is null means the entity
    /// exists but BCRA has never been fetched for it.
    /// </para>
    /// </summary>
    Task<EntitySnapshotProjection?> GetBcraSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns the STORED CASFOG snapshot of one entity, tenant-isolated, or null if there is no
    /// such entity in this tenant. A read of three columns; it does not consult CASFOG.
    /// </summary>
    Task<EntitySnapshotProjection?> GetCasfogSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns the STORED NOSIS snapshot of one entity, tenant-isolated, or null if there is no such
    /// entity in this tenant. A read of three columns; it does not consult NOSIS — which is billed
    /// per query, so reading the stored snapshot and re-querying the source must stay distinct.
    /// </summary>
    Task<EntitySnapshotProjection?> GetNosisSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns the STORED LUFE snapshot of one entity, tenant-isolated, or null if there is no such
    /// entity in this tenant. A read of three columns; it does not consult LUFE.
    /// <para>
    /// Added for the fiadores candidates endpoint, which needs <c>LufeDataJson</c> and nothing else:
    /// <see cref="GetDetailByIdAsync"/> would have served, but its projection also names
    /// FiscalDataJson, and <see cref="GetByIdAsync"/> would drag all five nvarchar(max) snapshots
    /// off disk to read one of them.
    /// </para>
    /// </summary>
    Task<EntitySnapshotProjection?> GetLufeSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct);
}
