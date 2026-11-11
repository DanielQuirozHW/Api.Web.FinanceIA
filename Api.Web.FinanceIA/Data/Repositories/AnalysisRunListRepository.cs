// Project : Api.Web.FinanceIA
// Created : 2026-11-03

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IAnalysisRunListRepository"/>. Every query is AsNoTracking,
/// carries the TenantId predicate itself (there is no global query filter) and projects — no
/// <c>nvarchar(max)</c> document or entity column is read.
/// </summary>
public sealed class AnalysisRunListRepository : IAnalysisRunListRepository
{
    private const string ProcessingStatus = "Processing";
    private const string FailedStatus     = "Failed";
    private const string CompletedStatus  = "Completed";
    private const string DocumentEntityType = "Document";

    // Bound on the audit rows one call reads: ≤50 documents, each with a handful of failures inside a
    // window of at most 30 days. Only a pathological history reaches it, and then the newest win.
    private const int MaxAuditRows = 500;

    private readonly FinanciaDbContext _dbContext;

    /// <summary>
    /// Initializes a new instance of <see cref="AnalysisRunListRepository"/>.
    /// </summary>
    public AnalysisRunListRepository(FinanciaDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AnalysisRunDocumentProjection>> GetRunningAsync(
        string tenantId, int take, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return await Project(
                BalanceDocuments(tenantId).Where(d => d.ProcessingStatus == ProcessingStatus), tenantId)
            .OrderByDescending(x => x.UploadedAt)
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AnalysisRunDocumentProjection>> GetFailedAsync(
        string tenantId, DateTime sinceUtc, int take, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return await Project(
                BalanceDocuments(tenantId)
                    .Where(d => d.ProcessingStatus == FailedStatus && d.ProcessedAt >= sinceUtc),
                tenantId)
            .OrderByDescending(x => x.ProcessedAt)
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AnalysisRunDocumentProjection>> GetCompletedAsync(
        string tenantId, DateTime sinceUtc, int take, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var documents = BalanceDocuments(tenantId)
            .Where(d => d.ProcessingStatus == CompletedStatus && d.ProcessedAt >= sinceUtc);

        // The latest BALANCE analysis of the document (a re-analysis after a rejection adds a newer
        // one; the rejected one stays as history), then its result. GetLatestByDocumentIdAsync's
        // ordering, as a correlated subquery so the page is one round trip.
        return await (
                from d in documents
                join e in _dbContext.Entities.Where(e => e.TenantId == tenantId)
                    on d.EntityId equals e.EntityId into entities
                from e in entities.DefaultIfEmpty()
                orderby d.ProcessedAt descending
                select new AnalysisRunDocumentProjection
                {
                    DocumentId             = d.DocumentId,
                    EntityId               = d.EntityId,
                    EntityName             = e == null ? null : e.BusinessName,
                    EntityPersonLegajoId   = e == null ? null : e.PersonLegajoId,
                    FileName               = d.OriginalFileName,
                    UploadedAt             = d.UploadedAt,
                    ProcessedAt            = d.ProcessedAt,
                    FileHashSha256         = d.FileHashSHA256,
                    LastExtractionError    = d.LastExtractionError,
                    LastExtractionFailedAt = d.LastExtractionFailedAt,
                    FiscalYear             = d.FiscalYear,
                    ResultId = _dbContext.Analyses
                        .Where(a => a.TenantId == tenantId
                                 && a.DocumentId == d.DocumentId
                                 && a.AnalysisKind == AnalysisKinds.Balance)
                        .OrderByDescending(a => a.CreatedAt)
                        .Select(a => _dbContext.AnalysisResults
                            .Where(r => r.TenantId == tenantId && r.AnalysisId == a.AnalysisId)
                            .Select(r => (Guid?)r.ResultId)
                            .FirstOrDefault())
                        .FirstOrDefault()
                })
            .AsNoTracking()
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, int>> SumExtractionAttemptsByHashesAsync(
        IReadOnlyCollection<string> fileHashes, string tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fileHashes);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // A blank hash is not a wildcard (see DocumentRepository.SumExtractionAttemptsByHashAsync).
        var hashes = fileHashes.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct().ToList();
        if (hashes.Count == 0)
            return new Dictionary<string, int>();

        var sums = await _dbContext.Documents
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && hashes.Contains(x.FileHashSHA256))
            .GroupBy(x => x.FileHashSHA256)
            .Select(g => new { Hash = g.Key, Attempts = g.Sum(x => x.ExtractionAttempts) })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Folded again in memory: the hashes are lowercase hex, but should two spellings ever differ only
        // in case, SQL's case-insensitive GROUP BY has already merged them and this keeps that answer.
        return sums
            .GroupBy(x => x.Hash, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Attempts), StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AnalysisRunFailureAuditProjection>> GetFailureAuditsAsync(
        IReadOnlyCollection<Guid> documentIds, string tenantId, DateTime sinceUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (documentIds.Count == 0)
            return [];

        // AuditLogs.EntityId is a string column holding the Guid's "D" format — what every
        // Analysis.Failed writer stores (documentId.ToString()).
        var entityIds = documentIds.Distinct().Select(id => id.ToString()).ToList();

        var rows = await _dbContext.AuditLogs
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId
                     && (a.Action == AnalysisRunFailureCodes.FailedAuditAction
                         || a.Action == AnalysisAutoRetryPolicy.FinishedAuditAction)
                     && a.EntityType == DocumentEntityType
                     && a.Timestamp >= sinceUtc
                     && a.EntityId != null
                     && entityIds.Contains(a.EntityId))
            .OrderByDescending(a => a.Timestamp)
            .Take(MaxAuditRows)
            .Select(a => new { a.EntityId, a.Action, a.Timestamp, a.UserId, a.NewValueJson })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .Where(r => Guid.TryParse(r.EntityId, out _))
            .Select(r => new AnalysisRunFailureAuditProjection
            {
                DocumentId   = Guid.Parse(r.EntityId!),
                Action       = r.Action,
                Timestamp    = r.Timestamp,
                UserId       = r.UserId,
                NewValueJson = r.NewValueJson
            })
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, string>> GetUserDisplayNamesAsync(
        IReadOnlyCollection<Guid> userIds, string tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, string>();

        var users = await _dbContext.Users
            .AsNoTracking()
            .Where(u => u.TenantId == tenantId && ids.Contains(u.UserId))
            .Select(u => new { u.UserId, u.DisplayName })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return users
            .Where(u => !string.IsNullOrWhiteSpace(u.DisplayName))
            .ToDictionary(u => u.UserId, u => u.DisplayName);
    }

    // Business balance documents of the tenant: not deleted, not a legajo document, of type Balance.
    // SQL Server's case-insensitive collation also matches the legacy "balance" spelling the old upload
    // stored as sent (see AnalysisController.ClassifyDocumentType).
    private IQueryable<Document> BalanceDocuments(string tenantId)
        => _dbContext.Documents
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId
                     && !d.IsDeleted
                     && d.PersonLegajoId == null
                     && d.LegajoStatus == null
                     && d.DocumentType == DocumentTypes.Balance);

    private IQueryable<AnalysisRunDocumentProjection> Project(IQueryable<Document> documents, string tenantId)
        => from d in documents
           join e in _dbContext.Entities.Where(e => e.TenantId == tenantId)
               on d.EntityId equals e.EntityId into entities
           from e in entities.DefaultIfEmpty()
           select new AnalysisRunDocumentProjection
           {
               DocumentId             = d.DocumentId,
               EntityId               = d.EntityId,
               EntityName             = e == null ? null : e.BusinessName,
               EntityPersonLegajoId   = e == null ? null : e.PersonLegajoId,
               FileName               = d.OriginalFileName,
               UploadedAt             = d.UploadedAt,
               ProcessedAt            = d.ProcessedAt,
               FileHashSha256         = d.FileHashSHA256,
               LastExtractionError    = d.LastExtractionError,
               LastExtractionFailedAt = d.LastExtractionFailedAt,
               FiscalYear             = d.FiscalYear
           };
}
