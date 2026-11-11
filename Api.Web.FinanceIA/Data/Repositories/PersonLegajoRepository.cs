// Project : Api.Web.FinanceIA
// Created : 2026-09-27

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IPersonLegajoRepository"/>.
/// Read queries use AsNoTracking and are tenant-scoped. Writes call
/// ChangeTracker.DetectChanges() manually because AutoDetectChangesEnabled is disabled on
/// <see cref="FinanciaDbContext"/>.
/// </summary>
public sealed class PersonLegajoRepository : IPersonLegajoRepository
{
    private readonly FinanciaDbContext _dbContext;
    private readonly ILogger<PersonLegajoRepository> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="PersonLegajoRepository"/>.
    /// </summary>
    public PersonLegajoRepository(FinanciaDbContext dbContext, ILogger<PersonLegajoRepository> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PersonLegajo?> GetByCuitAsync(string cuit, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cuit);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // SingleOrDefault is safe: UX_PersonLegajos_TenantId_CUIT makes (TenantId, CUIT) unique.
        return await _dbContext.PersonLegajos
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.CUIT == cuit && x.TenantId == tenantId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PersonLegajo?> GetByIdAsync(Guid personLegajoId, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return await _dbContext.PersonLegajos
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.PersonLegajoId == personLegajoId && x.TenantId == tenantId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> GetMonotributoCategoryAsync(
        Guid personLegajoId, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // Projected: the SELECT names one nvarchar(2) column, never the three nvarchar(max) snapshots.
        return await _dbContext.PersonLegajos
            .AsNoTracking()
            .Where(x => x.PersonLegajoId == personLegajoId && x.TenantId == tenantId)
            .Select(x => x.MonotributoCategory)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveEntityWithLegajoAsync(
        Entity entity,
        bool entityIsNew,
        PersonLegajo legajo,
        bool legajoIsNew,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(legajo);

        // Defence in depth: nothing may link an entity to another tenant's legajo, and the entity
        // must already carry the key — the FK is what orders the legajo INSERT before the entity's.
        if (!string.Equals(entity.TenantId, legajo.TenantId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The legajo belongs to another tenant.", nameof(legajo));
        }

        if (entity.PersonLegajoId != legajo.PersonLegajoId)
        {
            throw new ArgumentException("The entity does not point at this legajo.", nameof(entity));
        }

        // Registration order does not matter: EF orders the INSERTs by the modelled
        // FK_Entities_PersonLegajos, so a new legajo always precedes the entity that points at it.
        if (legajoIsNew)
            _dbContext.PersonLegajos.Add(legajo);
        else
            _dbContext.PersonLegajos.Update(legajo);

        if (entityIsNew)
            _dbContext.Entities.Add(entity);
        else
            _dbContext.Entities.Update(entity);

        _dbContext.ChangeTracker.DetectChanges();

        try
        {
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (UniqueViolation.Classify(ex) is { } scope)
        {
            // Detach both so the failed write does not poison a retry on this request-scoped
            // context (the same move AnalysisApprovalVoteRepository makes). The provider message is
            // deliberately not logged or carried on: it contains the duplicate key, i.e. the CUIL.
            _dbContext.Entry(entity).State = EntityState.Detached;
            _dbContext.Entry(legajo).State = EntityState.Detached;

            _logger.LogInformation(
                "Entity {EntityId} write lost a per-tenant CUIT uniqueness race ({DuplicateCuitScope}) for tenant {TenantId}",
                entity.EntityId, scope, entity.TenantId);

            throw new DuplicateCuitException(scope);
        }

        _logger.LogInformation(
            "Entity {EntityId} saved with legajo {PersonLegajoId} (new entity: {EntityIsNew}, new legajo: {LegajoIsNew}) for tenant {TenantId}",
            entity.EntityId, legajo.PersonLegajoId, entityIsNew, legajoIsNew, entity.TenantId);
    }
    /// <inheritdoc />
    public async Task SaveFiadorLegajoLinkAsync(
        Fiador fiador,
        PersonLegajo legajo,
        bool legajoIsNew,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fiador);
        ArgumentNullException.ThrowIfNull(legajo);

        // Defence in depth, as in SaveEntityWithLegajoAsync: never another tenant's legajo, and the
        // fiador must already carry the key the FK orders the INSERT by.
        if (!string.Equals(fiador.TenantId, legajo.TenantId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The legajo belongs to another tenant.", nameof(legajo));
        }

        if (fiador.PersonLegajoId != legajo.PersonLegajoId)
        {
            throw new ArgumentException("The fiador does not point at this legajo.", nameof(fiador));
        }

        if (legajoIsNew)
            _dbContext.PersonLegajos.Add(legajo);

        // Attached Unchanged, then only the link marked modified: the UPDATE names PersonLegajoId alone,
        // so an analyst's concurrent PATCH of the name or cargo is never overwritten with the values this
        // request read.
        _dbContext.Fiadores.Attach(fiador);
        _dbContext.Entry(fiador).Property(x => x.PersonLegajoId).IsModified = true;

        _dbContext.ChangeTracker.DetectChanges();

        try
        {
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (UniqueViolation.Classify(ex) is { } scope)
        {
            // Detached so the retry on this request-scoped context starts clean; the provider message
            // carries the duplicate key (the CUIL) and is never logged or carried on.
            _dbContext.Entry(fiador).State = EntityState.Detached;
            _dbContext.Entry(legajo).State = EntityState.Detached;

            _logger.LogInformation(
                "Fiador {FiadorId} legajo link lost a per-tenant CUIT uniqueness race ({DuplicateCuitScope}) for tenant {TenantId}",
                fiador.FiadorId, scope, fiador.TenantId);

            throw new DuplicateCuitException(scope);
        }

        _dbContext.Entry(fiador).State = EntityState.Detached;

        _logger.LogInformation(
            "Fiador {FiadorId} linked to legajo {PersonLegajoId} (new legajo: {LegajoIsNew}) for tenant {TenantId}",
            fiador.FiadorId, legajo.PersonLegajoId, legajoIsNew, fiador.TenantId);
    }

    /// <inheritdoc />
    public async Task SaveLegajoSnapshotsAsync(PersonLegajo legajo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(legajo);

        _dbContext.PersonLegajos.Attach(legajo);
        var entry = _dbContext.Entry(legajo);
        entry.Property(x => x.BcraDataJson).IsModified        = true;
        entry.Property(x => x.BcraDataUpdatedAt).IsModified   = true;
        entry.Property(x => x.ArcaSnapshotJson).IsModified    = true;
        entry.Property(x => x.ArcaDataUpdatedAt).IsModified   = true;
        entry.Property(x => x.FiscalRegime).IsModified        = true;
        entry.Property(x => x.MonotributoCategory).IsModified = true;
        entry.Property(x => x.ActivityStartDate).IsModified   = true;
        entry.Property(x => x.UpdatedAt).IsModified           = true;

        _dbContext.ChangeTracker.DetectChanges();

        try
        {
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            entry.State = EntityState.Detached;
        }

        _logger.LogInformation(
            "Legajo {PersonLegajoId} snapshots saved for tenant {TenantId}",
            legajo.PersonLegajoId, legajo.TenantId);
    }
}
