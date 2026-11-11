// Project : Api.Web.FinanceIA
// Created : 2026-07-20

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ICreditLineOperationRepository"/>.
/// All reads use AsNoTracking. CreateAsync loads a tracked entity and calls DetectChanges before
/// SaveChangesAsync; the two status transitions are single conditional ExecuteUpdateAsync statements
/// instead, because each has to be atomic with the audit columns it stamps.
/// </summary>
public sealed class CreditLineOperationRepository : ICreditLineOperationRepository
{
    private readonly FinanciaDbContext                      _dbContext;
    private readonly ILogger<CreditLineOperationRepository> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="CreditLineOperationRepository"/>.
    /// </summary>
    public CreditLineOperationRepository(
        FinanciaDbContext dbContext, ILogger<CreditLineOperationRepository> logger)
    {
        _dbContext = dbContext;
        _logger    = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CreditLineOperation>> GetByCreditLineAsync(
        Guid creditLineId, string tenantId, CancellationToken ct)
    {
        return await _dbContext.CreditLineOperations
            .AsNoTracking()
            .Where(o => o.CreditLineId == creditLineId && o.TenantId == tenantId)
            .OrderBy(o => o.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CreditLineOperation>> GetByAnalysisAsync(
        Guid analysisId, string tenantId, CancellationToken ct)
    {
        return await _dbContext.CreditLineOperations
            .AsNoTracking()
            .Where(o => o.AnalysisId == analysisId && o.TenantId == tenantId)
            .OrderBy(o => o.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CreditLineOperation?> GetByIdAsync(
        Guid operationId, string tenantId, CancellationToken ct)
    {
        return await _dbContext.CreditLineOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.OperationId == operationId && o.TenantId == tenantId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CreateAsync(CreditLineOperation op, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(op);

        _dbContext.CreditLineOperations.Add(op);
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Created credit line operation {OperationId} for credit line {CreditLineId} analysis {AnalysisId} tenant {TenantId}",
            op.OperationId, op.CreditLineId, op.AnalysisId, op.TenantId);
    }

    /// <inheritdoc />
    public async Task<bool> CancelAsync(
        Guid operationId, string tenantId, string expectedStatus, Guid cancelledByUserId,
        DateTime cancelledAtUtc, CancellationToken ct)
    {
        // Single atomic conditional UPDATE guarded on the expected current status, mirroring
        // ConfirmVigenciaAsync. Guarding merely on "not already 'Cancelada'" would be enough to keep
        // the write idempotent, but not enough to keep the audit honest: a confirmar-vigencia that
        // commits between the caller's read and this write would still match, and the caller would
        // record a close as a "se cayó". Matching 0 rows instead lets it re-read and audit what
        // actually happened. Status and both audit columns land in the same statement — there is no
        // window in which a row is 'Cancelada' with no record of who cancelled it.
        var affected = await _dbContext.CreditLineOperations
            .Where(o => o.OperationId == operationId
                     && o.TenantId    == tenantId
                     && o.Status      == expectedStatus)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(o => o.Status,       "Cancelada")
                .SetProperty(o => o.CanceladaEl,  cancelledAtUtc)
                .SetProperty(o => o.CanceladaPor, cancelledByUserId),
                ct)
            .ConfigureAwait(false);

        if (affected == 0)
            return false;

        _logger.LogInformation(
            "Credit line operation {OperationId} cancelled from {ExpectedStatus} by {CancelledByUserId} tenant {TenantId}",
            operationId, expectedStatus, cancelledByUserId, tenantId);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmVigenciaAsync(
        Guid operationId, string tenantId, Guid confirmedByUserId, DateTime confirmedAtUtc,
        CancellationToken ct)
    {
        // Single atomic conditional UPDATE guarded on the expected current status, mirroring
        // CreditLineRepository.SetStatusAsync: a concurrent transition that already moved the row off
        // 'Aprobada' matches 0 rows, so the caller 409s instead of clobbering it. Status and both
        // audit columns land in the same statement — there is no window in which a row is 'Vigente'
        // with no record of who confirmed it.
        var affected = await _dbContext.CreditLineOperations
            .Where(o => o.OperationId == operationId
                     && o.TenantId    == tenantId
                     && o.Status      == "Aprobada")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(o => o.Status,               "Vigente")
                .SetProperty(o => o.VigenteConfirmadaEl,  confirmedAtUtc)
                .SetProperty(o => o.VigenteConfirmadaPor, confirmedByUserId),
                ct)
            .ConfigureAwait(false);

        if (affected == 0)
            return false;

        _logger.LogInformation(
            "Credit line operation {OperationId} confirmed vigente by {ConfirmedByUserId} tenant {TenantId}",
            operationId, confirmedByUserId, tenantId);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> HasNonCancelledByAnalysisAsync(
        Guid analysisId, string tenantId, CancellationToken ct)
    {
        // Persisted status is 'Aprobada', 'Vigente' or 'Cancelada'; 'Vencida' is derived at read
        // time. So a stored status != 'Cancelada' is exactly a live operation — an Aprobada one
        // counts, because it already reserves cupo even though nobody has confirmed it in force.
        return await _dbContext.CreditLineOperations
            .AsNoTracking()
            .AnyAsync(o => o.AnalysisId == analysisId
                        && o.TenantId   == tenantId
                        && o.Status     != "Cancelada", ct)
            .ConfigureAwait(false);
    }
}
