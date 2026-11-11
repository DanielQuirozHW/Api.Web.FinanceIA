// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Linq.Expressions;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ILegajoDocumentRepository"/>.
/// Reads are AsNoTracking and tenant-scoped. Status transitions are single conditional
/// ExecuteUpdateAsync statements — the atomic claim, per the ef-core-patterns confirmation that
/// bypassing the change tracker is intended here (041's precedent). Consent and run writes are
/// tracked and call ChangeTracker.DetectChanges() manually because AutoDetectChangesEnabled is
/// disabled on <see cref="FinanciaDbContext"/>.
/// </summary>
public sealed class LegajoDocumentRepository : ILegajoDocumentRepository
{
    private readonly FinanciaDbContext _dbContext;
    private readonly ILogger<LegajoDocumentRepository> _logger;

    /// <summary>Initializes a new instance of <see cref="LegajoDocumentRepository"/>.</summary>
    public LegajoDocumentRepository(FinanciaDbContext dbContext, ILogger<LegajoDocumentRepository> logger)
    {
        _dbContext = dbContext;
        _logger    = logger;
    }

    /// <summary>
    /// The WHERE clause of every conditional transition: this document, this tenant, still in
    /// <paramref name="fromStatus"/>, not deleted. Exposed so a test can evaluate the claim predicate
    /// over in-memory rows (the InMemory provider cannot run ExecuteUpdateAsync).
    /// </summary>
    internal static Expression<Func<Document, bool>> InStatus(Guid documentId, string tenantId, string fromStatus)
        => d => d.DocumentId == documentId
             && d.TenantId == tenantId
             && d.LegajoStatus == fromStatus
             && !d.IsDeleted;

    /// <summary>
    /// The legajos with ACTIVE consent — THE one definition of the rule: consent recorded, and its
    /// AutorizacionDatos document still in the same legajo and tenant and not deleted. Captured as a
    /// variable by every query that needs it, which EF inlines as a subquery.
    /// </summary>
    private IQueryable<PersonLegajo> ActiveConsentLegajos()
        => _dbContext.PersonLegajos
            .AsNoTracking()
            .Where(l => l.ConsentRecordedAt != null
                     && l.ConsentDocumentId != null
                     && _dbContext.Documents.Any(d => d.DocumentId == l.ConsentDocumentId
                                                   && d.TenantId == l.TenantId
                                                   && d.PersonLegajoId == l.PersonLegajoId
                                                   && d.DocumentType == DocumentTypes.AutorizacionDatos
                                                   && !d.IsDeleted));

    /// <inheritdoc />
    public async Task<LegajoSummary?> GetLegajoSummaryAsync(Guid personLegajoId, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var active = ActiveConsentLegajos();

        // Projected: never selects FullName or the three nvarchar(max) snapshots.
        return await _dbContext.PersonLegajos
            .AsNoTracking()
            .Where(l => l.PersonLegajoId == personLegajoId && l.TenantId == tenantId)
            .Select(l => new LegajoSummary
            {
                PersonLegajoId      = l.PersonLegajoId,
                TenantId            = l.TenantId,
                EntityId            = _dbContext.Entities
                                          .Where(e => e.PersonLegajoId == l.PersonLegajoId && e.TenantId == l.TenantId)
                                          .Select(e => (Guid?)e.EntityId)
                                          .FirstOrDefault(),
                CUIT                = l.CUIT,
                FiscalRegime        = l.FiscalRegime,
                MonotributoCategory = l.MonotributoCategory,
                ConsentRecordedAt   = l.ConsentRecordedAt,
                ConsentDocumentId   = l.ConsentDocumentId,
                ConsentRecordedBy   = l.ConsentRecordedBy,
                HasActiveConsent    = active.Any(a => a.PersonLegajoId == l.PersonLegajoId)
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Guid?> FindActiveByHashAsync(
        Guid personLegajoId, string tenantId, string fileHashSha256, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (string.IsNullOrWhiteSpace(fileHashSha256))
            return null;

        return await _dbContext.Documents
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId
                     && d.PersonLegajoId == personLegajoId
                     && d.FileHashSHA256 == fileHashSha256
                     && !d.IsDeleted)
            .OrderBy(d => d.UploadedAt)
            .Select(d => (Guid?)d.DocumentId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Document>> GetLegajoDocumentsAsync(
        Guid personLegajoId, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return await _dbContext.Documents
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.PersonLegajoId == personLegajoId && !d.IsDeleted)
            .OrderByDescending(d => d.UploadedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> GetDocumentIdsInStatusAsync(
        Guid personLegajoId, string tenantId, string legajoStatus, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return await _dbContext.Documents
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId
                     && d.PersonLegajoId == personLegajoId
                     && d.LegajoStatus == legajoStatus
                     && !d.IsDeleted)
            .Select(d => d.DocumentId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, LegajoExtraction>> GetCurrentClassificationsAsync(
        IReadOnlyCollection<Guid> documentIds, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (documentIds.Count == 0)
            return new Dictionary<Guid, LegajoExtraction>();

        var runs = await _dbContext.LegajoExtractions
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId
                     && documentIds.Contains(r.DocumentId)
                     && r.Kind == LegajoExtractionKinds.Classification
                     && r.IsCurrent)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // One per document by UX_LegajoExtractions_Current; newest wins if the index were ever absent.
        return runs
            .GroupBy(r => r.DocumentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.CreatedAt).First());
    }

    /// <inheritdoc />
    public async Task<LegajoDocumentWork?> GetWorkAsync(Guid documentId, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var active = ActiveConsentLegajos();

        return await (
                from d in _dbContext.Documents.AsNoTracking()
                join l in _dbContext.PersonLegajos.AsNoTracking()
                    on d.PersonLegajoId equals (Guid?)l.PersonLegajoId
                where d.DocumentId == documentId && d.TenantId == tenantId && l.TenantId == tenantId
                select new LegajoDocumentWork
                {
                    DocumentId        = d.DocumentId,
                    TenantId          = d.TenantId,
                    PersonLegajoId    = l.PersonLegajoId,
                    DocumentType      = d.DocumentType,
                    StoragePath       = d.StoragePath,
                    FileHashSha256    = d.FileHashSHA256,
                    LegajoStatus      = d.LegajoStatus,
                    IsDeleted         = d.IsDeleted,
                    PeriodEnd         = d.PeriodEnd,
                    LegajoCuit        = l.CUIT,
                    HasActiveConsent  = active.Any(a => a.PersonLegajoId == l.PersonLegajoId)
                })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> HasConsentAsync(Guid personLegajoId, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return await ActiveConsentLegajos()
            .AnyAsync(l => l.PersonLegajoId == personLegajoId && l.TenantId == tenantId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<(Guid? PersonLegajoId, bool HasActiveConsent)> GetEntityLegajoConsentAsync(
        Guid entityId, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var legajoId = await _dbContext.Entities
            .AsNoTracking()
            .Where(e => e.EntityId == entityId && e.TenantId == tenantId)
            .Select(e => e.PersonLegajoId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (legajoId is null)
            return (null, false);

        return (legajoId, await HasConsentAsync(legajoId.Value, tenantId, ct).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<bool> IsConsentDocumentInUseAsync(Guid documentId, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return await _dbContext.PersonLegajos
            .AsNoTracking()
            .AnyAsync(l => l.TenantId == tenantId
                        && l.ConsentDocumentId == documentId
                        && l.ConsentRecordedAt != null, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Guid?> CreateLegajoDocumentAsync(Document document, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);

        _dbContext.Documents.Add(document);
        _dbContext.ChangeTracker.DetectChanges();

        try
        {
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (UniqueViolation.Is(ex, UniqueViolation.DocumentLegajoHash))
        {
            // Detach so the failed insert does not poison this request-scoped context (the #237 move).
            // The provider message is not logged: it carries the key values.
            _dbContext.Entry(document).State = EntityState.Detached;

            var existing = await FindActiveByHashAsync(
                    document.PersonLegajoId!.Value, document.TenantId, document.FileHashSHA256, ct)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Legajo document insert lost a duplicate-file race in legajo {PersonLegajoId} tenant {TenantId}",
                document.PersonLegajoId, document.TenantId);

            return existing ?? Guid.Empty;
        }

        _logger.LogInformation(
            "Legajo document created: {DocumentId} in legajo {PersonLegajoId} for tenant {TenantId}",
            document.DocumentId, document.PersonLegajoId, document.TenantId);

        return null;
    }

    /// <inheritdoc />
    public async Task<bool> TryTransitionAsync(
        Guid documentId, string tenantId, string fromStatus, string toStatus, DateTime now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var rows = await _dbContext.Documents
            .Where(InStatus(documentId, tenantId, fromStatus))
            .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.LegajoStatus, toStatus)
                    .SetProperty(d => d.LegajoStatusUpdatedAt, now),
                ct)
            .ConfigureAwait(false);

        return rows == 1;
    }

    /// <inheritdoc />
    public async Task<bool> CompleteClassificationAsync(
        Guid documentId, string tenantId, string toStatus, DateTime? periodEnd, string? engagementType,
        string? holderCuit, DateTime now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var rows = await _dbContext.Documents
            .Where(InStatus(documentId, tenantId, LegajoStatuses.Classifying))
            .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.LegajoStatus, toStatus)
                    .SetProperty(d => d.LegajoStatusUpdatedAt, now)
                    .SetProperty(d => d.PeriodEnd, periodEnd)
                    .SetProperty(d => d.EngagementType, engagementType)
                    .SetProperty(d => d.HolderCuit, holderCuit),
                ct)
            .ConfigureAwait(false);

        return rows == 1;
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmClassificationAsync(
        Guid documentId, string tenantId, string documentType, DateTime? periodEnd, DateTime now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);

        var rows = await _dbContext.Documents
            .Where(InStatus(documentId, tenantId, LegajoStatuses.NeedsReview))
            .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.LegajoStatus, LegajoStatuses.ReadyForExtraction)
                    .SetProperty(d => d.LegajoStatusUpdatedAt, now)
                    .SetProperty(d => d.DocumentType, documentType)
                    .SetProperty(d => d.PeriodEnd, d => periodEnd ?? d.PeriodEnd),
                ct)
            .ConfigureAwait(false);

        return rows == 1;
    }

    /// <inheritdoc />
    public async Task<bool> FailAsync(
        Guid documentId, string tenantId, string fromStatus, string errorCode, bool countsAsAttempt,
        DateTime now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var reason = errorCode.Length > 200 ? errorCode[..200] : errorCode;
        var increment = countsAsAttempt ? 1 : 0;

        // Read-modify-write in the database, as RecordExtractionFailureAsync does: two failures on the
        // same document cannot both read 1 and write 2.
        var rows = await _dbContext.Documents
            .Where(InStatus(documentId, tenantId, fromStatus))
            .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.LegajoStatus, LegajoStatuses.Failed)
                    .SetProperty(d => d.LegajoStatusUpdatedAt, now)
                    .SetProperty(d => d.ExtractionAttempts, d => d.ExtractionAttempts + increment)
                    .SetProperty(d => d.LastExtractionFailedAt, now)
                    .SetProperty(d => d.LastExtractionError, reason),
                ct)
            .ConfigureAwait(false);

        return rows == 1;
    }

    /// <inheritdoc />
    public async Task InsertRunAsync(LegajoExtraction run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.IsCurrent)
        {
            var previous = await _dbContext.LegajoExtractions
                .Where(r => r.TenantId == run.TenantId
                         && r.DocumentId == run.DocumentId
                         && r.Kind == run.Kind
                         && r.IsCurrent)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var old in previous)
                old.IsCurrent = false;
        }

        _dbContext.LegajoExtractions.Add(run);
        _dbContext.ChangeTracker.DetectChanges();

        // ONE SaveChanges: the UPDATEs releasing UX_LegajoExtractions_Current precede the INSERT
        // (same table, Modified before Added — see LegajoExtractionConfiguration).
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Legajo run {ExtractionId} ({Kind}, {Status}) stored for document {DocumentId} tenant {TenantId}",
            run.ExtractionId, run.Kind, run.Status, run.DocumentId, run.TenantId);
    }

    /// <inheritdoc />
    public async Task<bool> SetConsentAsync(
        Guid personLegajoId, string tenantId, Guid consentDocumentId, Guid recordedBy, DateTime now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var legajo = await _dbContext.PersonLegajos
            .FirstOrDefaultAsync(l => l.PersonLegajoId == personLegajoId && l.TenantId == tenantId, ct)
            .ConfigureAwait(false);

        if (legajo is null)
            return false;

        legajo.ConsentRecordedAt = now;
        legajo.ConsentDocumentId = consentDocumentId;
        legajo.ConsentRecordedBy = recordedBy;
        legajo.UpdatedAt         = now;

        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> ClearConsentAsync(Guid personLegajoId, string tenantId, DateTime now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var legajo = await _dbContext.PersonLegajos
            .FirstOrDefaultAsync(l => l.PersonLegajoId == personLegajoId && l.TenantId == tenantId, ct)
            .ConfigureAwait(false);

        if (legajo is null)
            return false;

        legajo.ConsentRecordedAt = null;
        legajo.ConsentDocumentId = null;
        legajo.ConsentRecordedBy = null;
        legajo.UpdatedAt         = now;

        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(Guid DocumentId, string TenantId, string LegajoStatus)>> GetRecoverableAsync(
        DateTime staleCutoff, DateTime? waitingCutoff, IReadOnlyCollection<string> extractableTypes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(extractableTypes);

        // Deliberately NOT tenant-scoped: the worker recovers every tenant's stranded documents in the
        // one business database the API binds. Each row is then handled under its own TenantId.
        var everyWaiting  = waitingCutoff is null;
        var waitingBefore = waitingCutoff ?? DateTime.MaxValue;
        var types         = extractableTypes.ToList();
        var active        = ActiveConsentLegajos();

        var rows = await _dbContext.Documents
            .AsNoTracking()
            .Where(d => d.PersonLegajoId != null
                     && !d.IsDeleted
                     && ((d.LegajoStatus == LegajoStatuses.Queued
                          && (everyWaiting || d.LegajoStatusUpdatedAt == null || d.LegajoStatusUpdatedAt < waitingBefore))
                      || ((d.LegajoStatus == LegajoStatuses.Classifying || d.LegajoStatus == LegajoStatuses.Extracting)
                          && (d.LegajoStatusUpdatedAt == null || d.LegajoStatusUpdatedAt < staleCutoff))
                      || (d.LegajoStatus == LegajoStatuses.ReadyForExtraction
                          && types.Contains(d.DocumentType)
                          && (everyWaiting || d.LegajoStatusUpdatedAt == null || d.LegajoStatusUpdatedAt < waitingBefore)
                          && active.Any(a => a.PersonLegajoId == d.PersonLegajoId && a.TenantId == d.TenantId))))
            .Select(d => new { d.DocumentId, d.TenantId, d.LegajoStatus })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(r => (r.DocumentId, r.TenantId, r.LegajoStatus!)).ToList();
    }

    // ── Phase 2b: extraction ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<bool> TryClaimForExtractionAsync(
        Guid documentId, string tenantId, IReadOnlyCollection<string> extractableTypes, DateTime now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(extractableTypes);

        var types = extractableTypes.ToList();

        var rows = await _dbContext.Documents
            .Where(InStatus(documentId, tenantId, LegajoStatuses.ReadyForExtraction))
            .Where(d => types.Contains(d.DocumentType))
            .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.LegajoStatus, LegajoStatuses.Extracting)
                    .SetProperty(d => d.LegajoStatusUpdatedAt, now),
                ct)
            .ConfigureAwait(false);

        return rows == 1;
    }

    /// <inheritdoc />
    public async Task<bool> CompleteExtractionAsync(
        Guid documentId, string tenantId, string toStatus, string? holderCuit, DateTime now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var rows = await _dbContext.Documents
            .Where(InStatus(documentId, tenantId, LegajoStatuses.Extracting))
            .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.LegajoStatus, toStatus)
                    .SetProperty(d => d.LegajoStatusUpdatedAt, now)
                    .SetProperty(d => d.HolderCuit, d => holderCuit ?? d.HolderCuit),
                ct)
            .ConfigureAwait(false);

        return rows == 1;
    }

    /// <inheritdoc />
    public async Task<LegajoExtraction?> GetCurrentExtractionAsync(Guid documentId, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return await _dbContext.LegajoExtractions
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId
                     && r.DocumentId == documentId
                     && r.Kind == LegajoExtractionKinds.Extraction
                     && r.IsCurrent)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, LegajoExtraction>> GetCurrentExtractionsAsync(
        IReadOnlyCollection<Guid> documentIds, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (documentIds.Count == 0)
            return new Dictionary<Guid, LegajoExtraction>();

        var runs = await _dbContext.LegajoExtractions
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId
                     && documentIds.Contains(r.DocumentId)
                     && r.Kind == LegajoExtractionKinds.Extraction
                     && r.IsCurrent)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return runs
            .GroupBy(r => r.DocumentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.CreatedAt).First());
    }

    /// <inheritdoc />
    public async Task<bool> HasCurrentExtractionAsync(Guid documentId, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return await _dbContext.LegajoExtractions
            .AsNoTracking()
            .AnyAsync(r => r.TenantId == tenantId
                        && r.DocumentId == documentId
                        && r.Kind == LegajoExtractionKinds.Extraction
                        && r.IsCurrent, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LegajoCorrection>> GetCorrectionsAsync(
        IReadOnlyCollection<Guid> extractionIds, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (extractionIds.Count == 0)
            return [];

        return await _dbContext.LegajoCorrections
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && extractionIds.Contains(c.ExtractionId))
            .OrderBy(c => c.CorrectedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> InsertCorrectionAndReopenAsync(
        LegajoCorrection correction, Guid documentId, string tenantId, DateTime now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(correction);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // EnableRetryOnFailure refuses a user transaction outside the execution strategy: the whole unit
        // is what gets retried.
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        var reopened = await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            _dbContext.LegajoCorrections.Add(correction);
            _dbContext.ChangeTracker.DetectChanges();
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

            var rows = await _dbContext.Documents
                .Where(InStatus(documentId, tenantId, LegajoStatuses.Ready))
                .ExecuteUpdateAsync(s => s
                        .SetProperty(d => d.LegajoStatus, LegajoStatuses.NeedsReview)
                        .SetProperty(d => d.LegajoStatusUpdatedAt, now),
                    ct)
                .ConfigureAwait(false);

            if (rows == 1)
            {
                await _dbContext.LegajoExtractions
                    .Where(r => r.ExtractionId == correction.ExtractionId && r.TenantId == tenantId)
                    .ExecuteUpdateAsync(s => s
                            .SetProperty(r => r.ReviewedBy, (Guid?)null)
                            .SetProperty(r => r.ReviewedAt, (DateTime?)null)
                            .SetProperty(r => r.ReviewOverride, (bool?)null)
                            .SetProperty(r => r.ReviewReason, (string?)null),
                        ct)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return rows == 1;
        }).ConfigureAwait(false);

        _logger.LogInformation(
            "Legajo correction {CorrectionId} stored for run {ExtractionId} tenant {TenantId} (document reopened: {Reopened})",
            correction.CorrectionId, correction.ExtractionId, tenantId, reopened);

        return reopened;
    }

    /// <inheritdoc />
    public async Task<bool> TryApproveExtractionAsync(
        Guid documentId, string tenantId, Guid extractionId, int expectedCorrections, Guid reviewedBy,
        bool isOverride, string? reason, DateTime now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            // One conditional UPDATE: the run must still be current and carry exactly the corrections the
            // reviewer saw. A correction committed in between changes the count and the approval loses.
            var rows = await _dbContext.Documents
                .Where(InStatus(documentId, tenantId, LegajoStatuses.NeedsReview))
                .Where(d => _dbContext.LegajoExtractions.Any(r => r.ExtractionId == extractionId
                                                              && r.TenantId == tenantId
                                                              && r.DocumentId == documentId
                                                              && r.Kind == LegajoExtractionKinds.Extraction
                                                              && r.IsCurrent))
                .Where(d => _dbContext.LegajoCorrections.Count(c => c.ExtractionId == extractionId
                                                                 && c.TenantId == tenantId) == expectedCorrections)
                .ExecuteUpdateAsync(s => s
                        .SetProperty(d => d.LegajoStatus, LegajoStatuses.Ready)
                        .SetProperty(d => d.LegajoStatusUpdatedAt, now),
                    ct)
                .ConfigureAwait(false);

            if (rows != 1)
                return false;

            await _dbContext.LegajoExtractions
                .Where(r => r.ExtractionId == extractionId && r.TenantId == tenantId)
                .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.ReviewedBy, (Guid?)reviewedBy)
                        .SetProperty(r => r.ReviewedAt, (DateTime?)now)
                        .SetProperty(r => r.ReviewOverride, (bool?)isOverride)
                        .SetProperty(r => r.ReviewReason, reason),
                    ct)
                .ConfigureAwait(false);

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }
}
