// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IAnalysisRunStateRepository"/>. Every write is a single
/// conditional <c>ExecuteUpdateAsync</c> — the WHERE clause is the compare-and-set — and reports
/// whether it touched the row.
/// </summary>
public sealed class AnalysisRunStateRepository : IAnalysisRunStateRepository
{
    private const string ProcessingStatus = "Processing";
    private const string FailedStatus     = "Failed";

    private readonly FinanciaDbContext _dbContext;

    /// <summary>
    /// Initializes a new instance of <see cref="AnalysisRunStateRepository"/>.
    /// </summary>
    public AnalysisRunStateRepository(FinanciaDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<bool> TryBeginProcessingAsync(
        Guid documentId, string tenantId, string expectedStatus, DateTime? expectedProcessedAt, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedStatus);

        // ProcessedAt is compared with C# null semantics, which EF translates null-safely
        // ("= @p OR (both NULL)"), so a never-processed Pending row matches its own NULL.
        var rows = await _dbContext.Documents
            .Where(x => x.DocumentId == documentId
                     && x.TenantId == tenantId
                     && !x.IsDeleted
                     && x.ProcessingStatus == expectedStatus
                     && x.ProcessedAt == expectedProcessedAt)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.ProcessingStatus, ProcessingStatus)
                    .SetProperty(x => x.ProcessedAt, (DateTime?)null),
                ct)
            .ConfigureAwait(false);

        return rows == 1;
    }

    /// <inheritdoc />
    public async Task<bool> TryRestoreAsync(
        Guid documentId, string tenantId, string status, DateTime? processedAt, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        var rows = await _dbContext.Documents
            .Where(x => x.DocumentId == documentId
                     && x.TenantId == tenantId
                     && x.ProcessingStatus == ProcessingStatus)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.ProcessingStatus, status)
                    .SetProperty(x => x.ProcessedAt, processedAt),
                ct)
            .ConfigureAwait(false);

        return rows == 1;
    }

    /// <inheritdoc />
    public async Task<bool> TryFailProcessingAsync(
        Guid documentId, string tenantId, DateTime failedAt, CancellationToken ct)
    {
        var rows = await _dbContext.Documents
            .Where(x => x.DocumentId == documentId
                     && x.TenantId == tenantId
                     && x.ProcessingStatus == ProcessingStatus)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.ProcessingStatus, FailedStatus)
                    .SetProperty(x => x.ProcessedAt, (DateTime?)failedAt),
                ct)
            .ConfigureAwait(false);

        return rows == 1;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProcessingDocument>> GetProcessingDocumentsAsync(CancellationToken ct)
    {
        // Cross-tenant by design — see the interface. Not a request path.
        return await _dbContext.Documents
            .AsNoTracking()
            .Where(x => x.ProcessingStatus == ProcessingStatus && !x.IsDeleted)
            .Select(x => new ProcessingDocument(x.DocumentId, x.TenantId, x.EntityId, x.UploadedBy))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}
