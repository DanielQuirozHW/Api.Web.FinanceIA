// Project : Api.Web.FinanceIA
// Created : 2026-08-28

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IFiadorRepository"/>.
/// All reads use AsNoTracking; the write path calls DetectChanges before SaveChangesAsync
/// (the context disables AutoDetectChanges), and the status transition is a single conditional
/// ExecuteUpdateAsync rather than a tracked load.
///
/// PII: <see cref="Fiador.CUIT"/> and <see cref="Fiador.Denominacion"/> name a natural person and
/// are NEVER written to a log line here — only FiadorId, EntityId and TenantId are.
/// </summary>
public sealed class FiadorRepository : IFiadorRepository
{
    private readonly FinanciaDbContext         _dbContext;
    private readonly ILogger<FiadorRepository> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="FiadorRepository"/>.
    /// </summary>
    public FiadorRepository(FinanciaDbContext dbContext, ILogger<FiadorRepository> logger)
    {
        _dbContext = dbContext;
        _logger    = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Fiador>> GetActiveByEntityAsync(
        Guid entityId, string tenantId, CancellationToken ct)
    {
        return await _dbContext.Fiadores
            .AsNoTracking()
            .Where(f => f.EntityId == entityId
                     && f.TenantId == tenantId
                     && f.Status   == "Active")
            .OrderBy(f => f.Denominacion)
            .ThenBy(f => f.CUIT)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Fiador?> GetActiveByEntityCuitAsync(
        Guid entityId, string cuit, string tenantId, CancellationToken ct)
    {
        return await _dbContext.Fiadores
            .AsNoTracking()
            .FirstOrDefaultAsync(
                f => f.EntityId == entityId
                  && f.CUIT     == cuit
                  && f.TenantId == tenantId
                  && f.Status   == "Active",
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Fiador?> GetByIdAsync(Guid fiadorId, string tenantId, CancellationToken ct)
    {
        return await _dbContext.Fiadores
            .AsNoTracking()
            .FirstOrDefaultAsync(
                f => f.FiadorId == fiadorId && f.TenantId == tenantId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CreateAsync(Fiador fiador, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fiador);

        _dbContext.Fiadores.Add(fiador);
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        // The CUIT and the denominación are deliberately absent from this line: they identify a
        // natural person, and the project rule forbids logging PII.
        _logger.LogInformation(
            "Created fiador {FiadorId} for entity {EntityId} tenant {TenantId}",
            fiador.FiadorId, fiador.EntityId, fiador.TenantId);
    }

    /// <inheritdoc />
    public async Task<bool> SetStatusAsync(
        Guid fiadorId, string tenantId, string expectedStatus, string newStatus, CancellationToken ct)
    {
        // Single atomic conditional UPDATE guarded on the expected current status: a concurrent
        // transition that already moved the row off expectedStatus matches 0 rows, so the caller
        // 409s instead of clobbering it. Only the Status column is written.
        var affected = await _dbContext.Fiadores
            .Where(f => f.FiadorId == fiadorId
                     && f.TenantId == tenantId
                     && f.Status   == expectedStatus)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(f => f.Status, newStatus),
                ct)
            .ConfigureAwait(false);

        if (affected == 0)
            return false;

        _logger.LogInformation(
            "Fiador {FiadorId} status {OldStatus} -> {NewStatus} tenant {TenantId}",
            fiadorId, expectedStatus, newStatus, tenantId);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> SetNosisSnapshotAsync(
        Guid fiadorId, string tenantId, string nosisDataJson, DateTime fetchedAt, CancellationToken ct)
    {
        // Guard clause, not a silent no-op: a blank payload here would mean the caller lost the
        // snapshot somewhere between the lookup and the write, and overwriting a good stored
        // snapshot with nothing is the one outcome this whole path is built to avoid.
        if (string.IsNullOrWhiteSpace(nosisDataJson))
            throw new ArgumentException("The NOSIS snapshot must not be blank.", nameof(nosisDataJson));

        // Single atomic conditional UPDATE writing only the two snapshot columns, guarded on
        // Status = 'Active'. A fiador removed by a concurrent request matches 0 rows, so the caller
        // 409s instead of stamping a snapshot onto a tombstone. Same shape as SetStatusAsync above.
        var affected = await _dbContext.Fiadores
            .Where(f => f.FiadorId == fiadorId
                     && f.TenantId == tenantId
                     && f.Status   == "Active")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(f => f.NosisDataJson,  nosisDataJson)
                .SetProperty(f => f.NosisFetchedAt, fetchedAt),
                ct)
            .ConfigureAwait(false);

        if (affected == 0)
            return false;

        // The snapshot itself is deliberately absent from this line: NosisData carries the fiador's
        // identity, their historia laboral, their employer's CUIT and the PEP/SO flags. Only its
        // length is recorded, which is enough to tell an empty write from a real one.
        _logger.LogInformation(
            "Stored NOSIS snapshot for fiador {FiadorId} tenant {TenantId} ({Length} chars)",
            fiadorId, tenantId, nosisDataJson.Length);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateDetailsAsync(
        Guid fiadorId, string tenantId, string? denominacion, string? cargo, decimal? porcAccionista,
        DateTime updatedAt, Guid updatedByUserId, CancellationToken ct)
    {
        // Same shape as SetStatusAsync: one atomic conditional UPDATE guarded on Status = 'Active', so a
        // concurrent removal matches 0 rows and the caller 409s instead of editing a tombstone.
        var affected = await _dbContext.Fiadores
            .Where(f => f.FiadorId == fiadorId
                     && f.TenantId == tenantId
                     && f.Status   == "Active")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(f => f.Denominacion,    denominacion)
                .SetProperty(f => f.Cargo,           cargo)
                .SetProperty(f => f.PorcAccionista,  porcAccionista)
                .SetProperty(f => f.UpdatedAt,       updatedAt)
                .SetProperty(f => f.UpdatedByUserId, updatedByUserId),
                ct)
            .ConfigureAwait(false);

        if (affected == 0)
            return false;

        // The edited values are deliberately absent: they name a natural person.
        _logger.LogInformation(
            "Updated details of fiador {FiadorId} tenant {TenantId}",
            fiadorId, tenantId);

        return true;
    }
    /// <inheritdoc />
    public async Task<bool> HasActiveFiadorWithLegajoAsync(
        Guid entityId, Guid personLegajoId, string tenantId, CancellationToken ct)
    {
        return await _dbContext.Fiadores
            .AsNoTracking()
            .AnyAsync(
                f => f.EntityId       == entityId
                  && f.PersonLegajoId == personLegajoId
                  && f.TenantId       == tenantId
                  && f.Status         == "Active",
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> AnyActiveWithLegajoAsync(Guid personLegajoId, string tenantId, CancellationToken ct)
    {
        return await _dbContext.Fiadores
            .AsNoTracking()
            .AnyAsync(
                f => f.PersonLegajoId == personLegajoId
                  && f.TenantId       == tenantId
                  && f.Status         == "Active",
                ct)
            .ConfigureAwait(false);
    }
}
