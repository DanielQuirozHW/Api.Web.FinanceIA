// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="INosisReportRepository"/>.
/// <para>
/// Writes load the entity TRACKED and set only the report columns, then call
/// <c>DetectChanges</c> (AutoDetectChanges is off on <see cref="FinanciaDbContext"/>), so the UPDATE
/// names those columns alone. A NOSIS, BCRA or CASFOG refresh running at the same moment is therefore
/// never overwritten by this write — unlike <c>EntityRepository.UpdateAsync</c>, which marks every
/// property modified.
/// </para>
/// </summary>
public sealed class NosisReportRepository : INosisReportRepository
{
    private readonly FinanciaDbContext _dbContext;
    private readonly ILogger<NosisReportRepository> _logger;

    /// <summary>Initializes a new instance of <see cref="NosisReportRepository"/>.</summary>
    public NosisReportRepository(FinanciaDbContext dbContext, ILogger<NosisReportRepository> logger)
    {
        _dbContext = dbContext;
        _logger    = logger;
    }

    /// <inheritdoc />
    public async Task<NosisReportSubject?> GetSubjectAsync(Guid entityId, string tenantId, CancellationToken ct)
    {
        return await SubjectQuery(entityId, tenantId)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<NosisReportSnapshot?> GetAsync(Guid entityId, string tenantId, CancellationToken ct)
    {
        return await SnapshotQuery(entityId, tenantId)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<(string? Sha256, string? ParserVersion)> GetStoredFingerprintAsync(
        Guid entityId, string tenantId, CancellationToken ct)
    {
        var row = await StoredFingerprintQuery(entityId, tenantId)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return row is null ? (null, null) : (row.Sha256, row.ParserVersion);
    }

    /// <inheritdoc />
    public async Task<bool> SaveAsync(
        Guid entityId, string tenantId, string json, DateTime reportDate, DateTime importedAt, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var entity = await TrackedEntityAsync(entityId, tenantId, ct).ConfigureAwait(false);
        if (entity is null)
            return false;

        entity.NosisReportJson       = json;
        entity.NosisReportDate       = DateTime.SpecifyKind(reportDate, DateTimeKind.Unspecified);
        entity.NosisReportImportedAt = DateTime.SpecifyKind(importedAt, DateTimeKind.Utc);

        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "NOSIS report stored for entity {EntityId} tenant {TenantId}", entityId, tenantId);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> ClearAsync(Guid entityId, string tenantId, CancellationToken ct)
    {
        var entity = await TrackedEntityAsync(entityId, tenantId, ct).ConfigureAwait(false);
        if (entity is null)
            return false;

        if (entity.NosisReportJson is null && entity.NosisReportDate is null && entity.NosisReportImportedAt is null)
            return false;

        entity.NosisReportJson       = null;
        entity.NosisReportDate       = null;
        entity.NosisReportImportedAt = null;

        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "NOSIS report cleared for entity {EntityId} tenant {TenantId}", entityId, tenantId);

        return true;
    }

    private Task<Entity?> TrackedEntityAsync(Guid entityId, string tenantId, CancellationToken ct)
        => _dbContext.Entities
            .AsTracking()
            .Where(x => x.EntityId == entityId && x.TenantId == tenantId)
            .SingleOrDefaultAsync(ct);

    // ── Test seam ─────────────────────────────────────────────────────────────────
    // Internal (InternalsVisibleTo: Api.Web.FinanceIA.Tests) so a test can call ToQueryString() and
    // assert the SQL the production reads emit.

    internal IQueryable<NosisReportSubject> SubjectQuery(Guid entityId, string tenantId)
        => _dbContext.Entities
            .AsNoTracking()
            .Where(x => x.EntityId == entityId && x.TenantId == tenantId)
            .Select(x => new NosisReportSubject
            {
                EntityId   = x.EntityId,
                CUIT       = x.CUIT,
                PersonType = x.PersonType
            });

    internal IQueryable<NosisReportSnapshot> SnapshotQuery(Guid entityId, string tenantId)
        => _dbContext.Entities
            .AsNoTracking()
            .Where(x => x.EntityId == entityId && x.TenantId == tenantId)
            .Select(x => new NosisReportSnapshot
            {
                EntityId   = x.EntityId,
                Json       = x.NosisReportJson,
                ReportDate = x.NosisReportDate,
                ImportedAt = x.NosisReportImportedAt
            });

    internal IQueryable<NosisReportFingerprint> StoredFingerprintQuery(Guid entityId, string tenantId)
        => _dbContext.Entities
            .AsNoTracking()
            .Where(x => x.EntityId == entityId && x.TenantId == tenantId)
            .Select(x => new NosisReportFingerprint
            {
                Sha256        = SqlJsonFunctions.JsonValue(x.NosisReportJson, "$.fileSha256"),
                ParserVersion = SqlJsonFunctions.JsonValue(x.NosisReportJson, "$.parserVersion")
            });
}
