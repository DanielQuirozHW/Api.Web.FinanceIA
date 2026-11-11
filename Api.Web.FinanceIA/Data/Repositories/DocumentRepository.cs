// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-05-04

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IDocumentRepository"/>.
/// Read queries use AsNoTracking. Write operations call
/// ChangeTracker.DetectChanges() manually because AutoDetectChangesEnabled
/// is disabled on <see cref="FinanciaDbContext"/>.
/// </summary>
public sealed class DocumentRepository : IDocumentRepository
{
    private readonly FinanciaDbContext _dbContext;
    private readonly ILogger<DocumentRepository> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="DocumentRepository"/>.
    /// </summary>
    public DocumentRepository(FinanciaDbContext dbContext, ILogger<DocumentRepository> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Document?> GetByIdAsync(Guid documentId, string tenantId, CancellationToken ct)
    {
        return await _dbContext.Documents
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.DocumentId == documentId && x.TenantId == tenantId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Document>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
    {
        return await _dbContext.Documents
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Document>> GetByEntityIdAsync(Guid entityId, string tenantId, CancellationToken ct)
    {
        return await _dbContext.Documents
            .AsNoTracking()
            .Where(x => x.EntityId == entityId && x.TenantId == tenantId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Document?> FindRecentByHashAsync(
        Guid entityId,
        string tenantId,
        string? fileHashSha256,
        string documentType,
        DateTime uploadedSinceUtc,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // A blank hash is not a wildcard (see SumExtractionAttemptsByHashAsync).
        if (string.IsNullOrWhiteSpace(fileHashSha256) || string.IsNullOrWhiteSpace(documentType))
            return null;

        var hash = fileHashSha256.Trim();
        var type = documentType.Trim();

        // SQL Server's case-insensitive collation already folds the hash and the type; the in-memory pass
        // below re-applies the same rule so the answer does not depend on the provider's collation. The
        // window is seconds wide, so only a handful of rows can ever come back.
        var candidates = await _dbContext.Documents
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId
                     && x.EntityId == entityId
                     && !x.IsDeleted
                     && x.PersonLegajoId == null
                     && x.FileHashSHA256 == hash
                     && x.DocumentType == type
                     && x.UploadedAt >= uploadedSinceUtc)
            .OrderByDescending(x => x.UploadedAt)
            .ThenByDescending(x => x.DocumentId)
            .Take(10)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return candidates.FirstOrDefault(x =>
            !string.IsNullOrWhiteSpace(x.FileHashSHA256)
            && string.Equals(x.FileHashSHA256, hash, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.DocumentType, type, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async Task<Document> CreateAsync(Document document, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);

        _dbContext.Documents.Add(document);
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Document created: {DocumentId} for tenant {TenantId}",
            document.DocumentId,
            document.TenantId);

        return document;
    }

    /// <inheritdoc />
    public async Task UpdateStatusAsync(
        Guid documentId,
        string tenantId,
        string status,
        DateTime? processedAt,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        await _dbContext.Documents
            .Where(x => x.DocumentId == documentId && x.TenantId == tenantId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.ProcessingStatus, status)
                    .SetProperty(x => x.ProcessedAt, processedAt),
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> SoftDeleteAsync(
        Guid documentId,
        string tenantId,
        DateTime deletedAt,
        CancellationToken ct)
    {
        var rows = await _dbContext.Documents
            .Where(x => x.DocumentId == documentId && x.TenantId == tenantId && !x.IsDeleted)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.IsDeleted, true)
                    .SetProperty(x => x.DeletedAt, deletedAt),
                ct)
            .ConfigureAwait(false);

        return rows > 0;
    }

    /// <inheritdoc />
    public async Task<int> SumExtractionAttemptsByHashAsync(
        string? fileHashSha256,
        string tenantId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // A blank hash is not a wildcard. Documents.FileHashSHA256 is NOT NULL but is only as
        // trustworthy as whatever wrote it; matching on "" would sum a whole tenant's failures
        // onto one unrelated file, so the answer for a hash we do not have is 0.
        if (string.IsNullOrWhiteSpace(fileHashSha256))
            return 0;

        // Sum, not Count: the budget is attempts, and one document row accumulates several.
        // Deleted rows are excluded — an analyst who removed the document is not still spending
        // against it. SumAsync over an empty set returns 0, so no null handling is needed.
        return await _dbContext.Documents
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId
                     && x.FileHashSHA256 == fileHashSha256
                     && !x.IsDeleted)
            .SumAsync(x => x.ExtractionAttempts, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> GetLatestExtractionErrorByHashAsync(
        string? fileHashSha256,
        string tenantId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // A blank hash is not a wildcard (see SumExtractionAttemptsByHashAsync).
        if (string.IsNullOrWhiteSpace(fileHashSha256))
            return null;

        // One scalar: the newest failure's code. DocumentId breaks a tie so the answer is stable.
        return await _dbContext.Documents
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId
                     && x.FileHashSHA256 == fileHashSha256
                     && !x.IsDeleted
                     && x.LastExtractionFailedAt != null)
            .OrderByDescending(x => x.LastExtractionFailedAt)
            .ThenByDescending(x => x.DocumentId)
            .Select(x => x.LastExtractionError)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RecordExtractionFailureAsync(
        Guid documentId,
        string tenantId,
        DateTime failedAt,
        string? error,
        bool countsAsAttempt,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // Defence in depth against a caller passing something long: the column is NVARCHAR(200)
        // and a truncation error here would surface inside a failure handler, which is the worst
        // possible place for a second exception.
        var reason = string.IsNullOrWhiteSpace(error)
            ? null
            : (error.Length > 200 ? error[..200] : error);

        var increment = countsAsAttempt ? 1 : 0;

        // Read-modify-write in the database, not in memory: two concurrent triggers on the same
        // document cannot both read 1 and both write 2.
        await _dbContext.Documents
            .Where(x => x.DocumentId == documentId && x.TenantId == tenantId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.ExtractionAttempts, x => x.ExtractionAttempts + increment)
                    .SetProperty(x => x.LastExtractionFailedAt, failedAt)
                    .SetProperty(x => x.LastExtractionError, reason),
                ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Extraction failure recorded for document {DocumentId} tenant {TenantId}: {ExtractionError} (counts_as_attempt={CountsAsAttempt})",
            documentId, tenantId, reason ?? "unspecified", countsAsAttempt);
    }
}
