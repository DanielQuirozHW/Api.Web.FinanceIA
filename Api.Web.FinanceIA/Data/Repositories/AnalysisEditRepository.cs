// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>EF Core implementation of <see cref="IAnalysisEditRepository"/> against FinanciaDbContext.</summary>
public sealed class AnalysisEditRepository : IAnalysisEditRepository
{
    private readonly FinanciaDbContext _dbContext;

    /// <summary>Initializes a new instance of <see cref="AnalysisEditRepository"/>.</summary>
    public AnalysisEditRepository(FinanciaDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task AddRangeAsync(IReadOnlyList<AnalysisEdit> edits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(edits);

        if (edits.Count == 0)
            return;

        // An execution-strategy retry of the caller's transaction re-runs this call with the same
        // instances, possibly still tracked from the failed attempt: forget them first.
        foreach (var tracked in _dbContext.ChangeTracker.Entries<AnalysisEdit>()
                     .Where(e => edits.Contains(e.Entity)).ToList())
            tracked.State = EntityState.Detached;

        foreach (var edit in edits)
            edit.AnalysisEditId = 0;   // IDENTITY: a value left by a failed attempt would be sent as explicit

        _dbContext.AnalysisEdits.AddRange(edits);
        try
        {
            _dbContext.ChangeTracker.DetectChanges();   // AutoDetectChanges is disabled on the context
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            foreach (var tracked in _dbContext.ChangeTracker.Entries<AnalysisEdit>()
                         .Where(e => edits.Contains(e.Entity)).ToList())
                tracked.State = EntityState.Detached;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AnalysisEditHistoryItem>> GetByAnalysisAsync(
        Guid analysisId, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return await _dbContext.AnalysisEdits
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.AnalysisId == analysisId)
            .OrderByDescending(e => e.EditedAt)
            .ThenByDescending(e => e.AnalysisEditId)
            .Select(e => new AnalysisEditHistoryItem(
                e.AnalysisEditId, e.FieldPath, e.OldValue, e.NewValue, e.EditReason, e.EditedBy, e.EditedAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}
