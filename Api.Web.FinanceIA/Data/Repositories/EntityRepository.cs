// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-05-04

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IEntityRepository"/>.
/// Read queries use AsNoTracking. Write operations call
/// ChangeTracker.DetectChanges() manually because AutoDetectChangesEnabled
/// is disabled on <see cref="FinanciaDbContext"/>.
/// </summary>
public sealed class EntityRepository : IEntityRepository
{
    private readonly FinanciaDbContext _dbContext;
    private readonly ILogger<EntityRepository> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="EntityRepository"/>.
    /// </summary>
    public EntityRepository(FinanciaDbContext dbContext, ILogger<EntityRepository> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Entity?> GetByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
    {
        return await _dbContext.Entities
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.EntityId == entityId && x.TenantId == tenantId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
    {
        // AnyAsync, not a projected SingleOrDefault: the emitted EXISTS names no column, so none of
        // the five nvarchar(max) snapshots is read off disk to answer a boolean. See the interface
        // doc for why that mattered.
        return await _dbContext.Entities
            .AsNoTracking()
            .AnyAsync(x => x.EntityId == entityId && x.TenantId == tenantId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Entity>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
    {
        return await _dbContext.Entities
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Entity?> GetByCuitAsync(string cuit, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cuit);

        return await _dbContext.Entities
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.CUIT == cuit && x.TenantId == tenantId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Entity> CreateAsync(Entity entity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);

        _dbContext.Entities.Add(entity);
        _dbContext.ChangeTracker.DetectChanges();
        await SaveOrThrowDuplicateAsync(entity, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Entity created: {EntityId} for tenant {TenantId}",
            entity.EntityId,
            entity.TenantId);

        return entity;
    }

    /// <inheritdoc />
    public async Task<Entity> UpdateAsync(Entity entity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);

        _dbContext.Entities.Update(entity);
        _dbContext.ChangeTracker.DetectChanges();
        await SaveOrThrowDuplicateAsync(entity, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Entity updated: {EntityId} for tenant {TenantId}",
            entity.EntityId,
            entity.TenantId);

        return entity;
    }

    /// <summary>
    /// SaveChanges, turning a lost race on a per-tenant CUIT index (DDL 069) into
    /// <see cref="DuplicateCuitException"/> so the controller can answer 409 instead of 500.
    /// The pre-check read in the controller cannot close that race; the index decides.
    /// Any other failure propagates unchanged.
    /// </summary>
    private async Task SaveOrThrowDuplicateAsync(Entity entity, CancellationToken ct)
    {
        try
        {
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (UniqueViolation.Classify(ex) is { } scope)
        {
            // Detached so the failed write does not poison a later SaveChanges on this context. The
            // provider message is not carried on: it holds the duplicate key, i.e. the CUIT.
            _dbContext.Entry(entity).State = EntityState.Detached;

            _logger.LogInformation(
                "Entity {EntityId} write lost a per-tenant CUIT uniqueness race ({DuplicateCuitScope}) for tenant {TenantId}",
                entity.EntityId, scope, entity.TenantId);

            throw new DuplicateCuitException(scope);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, EntityNameProjection>> GetNameLookupByIdsAsync(
        IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
    {
        var ids = entityIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, EntityNameProjection>();

        var rows = await BuildName(TenantSource(tenantId).Where(x => ids.Contains(x.EntityId)))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // EntityId is the primary key, so this is 1:1 by construction.
        return rows.ToDictionary(r => r.EntityId);
    }

    /// <inheritdoc />
    public async Task<EntityNameProjection?> GetNameByIdAsync(
        Guid entityId, string tenantId, CancellationToken ct)
    {
        return await BuildName(EntityByIdSource(entityId, tenantId))
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The name projection: the key, the two identifying strings and the person type, nothing else. The single
    /// declaration of that SELECT — both the by-ids lookup above and the single-id read compose it,
    /// so the set of columns cannot drift between them and neither can silently widen to a blob.
    ///
    /// <para>
    /// Neither caller reads a source snapshot, so naming FiscalDataJson, LufeDataJson, BcraDataJson,
    /// CasfogDataJson or NosisDataJson here would put five <c>nvarchar(max)</c> columns back on two
    /// paths that display a name.
    /// </para>
    /// </summary>
    private static IQueryable<EntityNameProjection> BuildName(IQueryable<Entity> source)
        => source
            .Select(x => new EntityNameProjection
            {
                EntityId     = x.EntityId,
                BusinessName = x.BusinessName,
                CUIT         = x.CUIT,
                PersonType   = x.PersonType
            });

    // ── List paging ───────────────────────────────────────────────────────────────
    // An EF .Select(...) so the emitted SQL names only the thirteen columns the list renders. Without
    // it the query drags all five nvarchar(max) source snapshots off disk for every row in the
    // tenant — which it did even after #138 stopped deserialising them.

    /// <inheritdoc />
    public async Task<(IReadOnlyList<EntityListProjection> Items, int TotalCount)> GetListPageByTenantAsync(
        string tenantId, int skip, int take, CancellationToken ct)
    {
        var source = TenantSource(tenantId);

        var total = await source.CountAsync(ct).ConfigureAwait(false);

        var items = await BuildListPage(source, skip, take)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return (items, total);
    }

    /// <summary>The tenant's entities, unordered and unpaged. The filter the COUNT also runs on.</summary>
    private IQueryable<Entity> TenantSource(string tenantId)
        => _dbContext.Entities
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId);

    /// <summary>
    /// The projection, the ordering and the paging. Naming only these thirteen columns is what stops
    /// SQL Server reading five nvarchar(max) columns off disk per row.
    /// </summary>
    private static IQueryable<EntityListProjection> BuildListPage(
        IQueryable<Entity> source, int skip, int take)
        => source
            .Select(x => new EntityListProjection
            {
                EntityId           = x.EntityId,
                CUIT               = x.CUIT,
                BusinessName       = x.BusinessName,
                TradeName          = x.TradeName,
                Sector             = x.Sector,
                MipymeCategory     = x.MipymeCategory,
                IsMonitored        = x.IsMonitored,
                RiskScore          = x.RiskScore,
                RiskScoreUpdatedAt = x.RiskScoreUpdatedAt,
                CreatedAt          = x.CreatedAt,
                CreatedBy          = x.CreatedBy,
                PersonType         = x.PersonType,
                FiscalRegime       = x.FiscalRegime
            })
            .OrderByDescending(p => p.CreatedAt)
            .ThenBy(p => p.EntityId)   // unique tie-break — see the interface doc
            .Skip(skip)
            .Take(take);

    // ── Detail and on-demand snapshots ────────────────────────────────────────────
    // The same argument as the list paging above, applied to GET /api/entities/{id}. The detail view
    // opens on a tab that renders FISCAL and LUFE; BCRA, CASFOG and NOSIS render only when the user
    // opens theirs. GetByIdAsync reads all five nvarchar(max) blobs, so the detail request paid the
    // disk read and the parse for three snapshots most requests never display — NOSIS and CASFOG
    // among them, the two heaviest. BuildDetail names two blobs instead of five; the other three are
    // served, when actually asked for, by the single-blob reads below. GetByIdAsync is untouched:
    // the write paths need the whole row.

    /// <inheritdoc />
    public async Task<EntityDetailProjection?> GetDetailByIdAsync(
        Guid entityId, string tenantId, CancellationToken ct)
    {
        return await BuildDetail(EntityByIdSource(entityId, tenantId), LegajoSource())
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EntitySnapshotProjection?> GetBcraSnapshotAsync(
        Guid entityId, string tenantId, CancellationToken ct)
    {
        return await BuildBcraSnapshot(EntityByIdSource(entityId, tenantId))
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EntitySnapshotProjection?> GetCasfogSnapshotAsync(
        Guid entityId, string tenantId, CancellationToken ct)
    {
        return await BuildCasfogSnapshot(EntityByIdSource(entityId, tenantId))
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EntitySnapshotProjection?> GetNosisSnapshotAsync(
        Guid entityId, string tenantId, CancellationToken ct)
    {
        return await BuildNosisSnapshot(EntityByIdSource(entityId, tenantId))
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EntitySnapshotProjection?> GetLufeSnapshotAsync(
        Guid entityId, string tenantId, CancellationToken ct)
    {
        return await BuildLufeSnapshot(EntityByIdSource(entityId, tenantId))
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// One entity, tenant-scoped. The filter every detail and snapshot read composes on, so tenant
    /// isolation is declared once and cannot drift between them.
    /// </summary>
    private IQueryable<Entity> EntityByIdSource(Guid entityId, string tenantId)
        => _dbContext.Entities
            .AsNoTracking()
            .Where(x => x.EntityId == entityId && x.TenantId == tenantId);

    /// <summary>
    /// The legajos a detail read may look into. Only ever composed into <see cref="BuildDetail"/>'s
    /// correlated subquery, which names one column of it.
    /// </summary>
    private IQueryable<PersonLegajo> LegajoSource()
        => _dbContext.PersonLegajos.AsNoTracking();

    /// <summary>
    /// The detail projection. Naming FiscalDataJson and LufeDataJson but NOT BcraDataJson,
    /// CasfogDataJson or NosisDataJson is what stops SQL Server reading three nvarchar(max) columns
    /// for a view that does not render them on load. The three <c>*DataUpdatedAt</c> columns stay:
    /// datetime2, free to read, and the detail still shows when each source was last refreshed.
    ///
    /// <para>
    /// <c>MonotributoCategory</c> lives on the persona humana's legajo (DDL 069) and is read with a
    /// correlated subquery that names that ONE nvarchar(2) column — never the legajo's three
    /// nvarchar(max) snapshots — and repeats the tenant predicate, so a legajo can only surface
    /// under an entity of its own tenant. There is no navigation to join through, on purpose (see
    /// <see cref="Entity.PersonLegajoId"/>).
    /// </para>
    /// </summary>
    private static IQueryable<EntityDetailProjection> BuildDetail(
        IQueryable<Entity> source, IQueryable<PersonLegajo> legajos)
        => source
            .Select(x => new EntityDetailProjection
            {
                EntityId            = x.EntityId,
                TenantId            = x.TenantId,
                CUIT                = x.CUIT,
                BusinessName        = x.BusinessName,
                TradeName           = x.TradeName,
                Sector              = x.Sector,
                MipymeCategory      = x.MipymeCategory,
                SgrOneClickEntityId = x.SgrOneClickEntityId,
                RiskScore           = x.RiskScore,
                RiskScoreUpdatedAt  = x.RiskScoreUpdatedAt,
                IsMonitored         = x.IsMonitored,
                CreatedAt           = x.CreatedAt,
                UpdatedAt           = x.UpdatedAt,
                CreatedBy           = x.CreatedBy,
                FiscalDataJson      = x.FiscalDataJson,
                FiscalDataUpdatedAt = x.FiscalDataUpdatedAt,
                LufeDataJson        = x.LufeDataJson,
                LufeDataUpdatedAt   = x.LufeDataUpdatedAt,
                BcraDataUpdatedAt   = x.BcraDataUpdatedAt,
                CasfogDataUpdatedAt = x.CasfogDataUpdatedAt,
                NosisDataUpdatedAt  = x.NosisDataUpdatedAt,
                PersonType          = x.PersonType,
                FiscalRegime        = x.FiscalRegime,
                PersonLegajoId      = x.PersonLegajoId,
                MonotributoCategory = legajos
                    .Where(l => l.PersonLegajoId == x.PersonLegajoId && l.TenantId == x.TenantId)
                    .Select(l => l.MonotributoCategory)
                    .FirstOrDefault()
            });

    // One projection per source rather than a parameterised one: the column has to be named in the
    // expression tree for EF to translate it, and three explicit Selects read better than an
    // Expression built by a switch. Each names exactly one blob, so opening the BCRA tab does not
    // pay for the NOSIS snapshot.

    private static IQueryable<EntitySnapshotProjection> BuildBcraSnapshot(IQueryable<Entity> source)
        => source.Select(x => new EntitySnapshotProjection
        {
            EntityId  = x.EntityId,
            Json      = x.BcraDataJson,
            UpdatedAt = x.BcraDataUpdatedAt
        });

    private static IQueryable<EntitySnapshotProjection> BuildCasfogSnapshot(IQueryable<Entity> source)
        => source.Select(x => new EntitySnapshotProjection
        {
            EntityId  = x.EntityId,
            Json      = x.CasfogDataJson,
            UpdatedAt = x.CasfogDataUpdatedAt
        });

    private static IQueryable<EntitySnapshotProjection> BuildNosisSnapshot(IQueryable<Entity> source)
        => source.Select(x => new EntitySnapshotProjection
        {
            EntityId  = x.EntityId,
            Json      = x.NosisDataJson,
            UpdatedAt = x.NosisDataUpdatedAt
        });

    private static IQueryable<EntitySnapshotProjection> BuildLufeSnapshot(IQueryable<Entity> source)
        => source.Select(x => new EntitySnapshotProjection
        {
            EntityId  = x.EntityId,
            Json      = x.LufeDataJson,
            UpdatedAt = x.LufeDataUpdatedAt
        });

    // ── Test seam ─────────────────────────────────────────────────────────────────
    // Internal (InternalsVisibleTo: Api.Web.FinanceIA.Tests) so a test can call ToQueryString() and
    // assert the SQL this exact query emits. It composes the SAME source builder and the SAME
    // BuildListPage the public method above runs, so the assertion is against the production query
    // rather than a re-declared copy of it that could drift.

    internal IQueryable<EntityListProjection> TenantListPageQuery(string tenantId, int skip, int take)
        => BuildListPage(TenantSource(tenantId), skip, take);

    // Same reasoning for the detail and snapshot reads: these compose the SAME EntityByIdSource and
    // the SAME Build* the public methods run, so a shape test asserts the production SQL.

    internal IQueryable<EntityDetailProjection> DetailQuery(Guid entityId, string tenantId)
        => BuildDetail(EntityByIdSource(entityId, tenantId), LegajoSource());

    internal IQueryable<EntitySnapshotProjection> BcraSnapshotQuery(Guid entityId, string tenantId)
        => BuildBcraSnapshot(EntityByIdSource(entityId, tenantId));

    internal IQueryable<EntitySnapshotProjection> CasfogSnapshotQuery(Guid entityId, string tenantId)
        => BuildCasfogSnapshot(EntityByIdSource(entityId, tenantId));

    internal IQueryable<EntitySnapshotProjection> NosisSnapshotQuery(Guid entityId, string tenantId)
        => BuildNosisSnapshot(EntityByIdSource(entityId, tenantId));

    internal IQueryable<EntityNameProjection> NameByIdQuery(Guid entityId, string tenantId)
        => BuildName(EntityByIdSource(entityId, tenantId));
}
