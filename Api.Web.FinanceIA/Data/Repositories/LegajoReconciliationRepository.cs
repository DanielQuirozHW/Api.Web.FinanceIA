// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Legajo;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ILegajoReconciliationRepository"/>. Reads are AsNoTracking and
/// tenant-scoped. Writes are tracked and call ChangeTracker.DetectChanges() manually because
/// AutoDetectChangesEnabled is disabled on <see cref="FinanciaDbContext"/>; no ExecuteUpdate/Delete is used,
/// so a rebuild is one SaveChanges — one transaction — and the InMemory provider can test it.
/// </summary>
public sealed class LegajoReconciliationRepository : ILegajoReconciliationRepository
{
    private readonly FinanciaDbContext _dbContext;

    /// <summary>Initializes a new instance of <see cref="LegajoReconciliationRepository"/>.</summary>
    public LegajoReconciliationRepository(FinanciaDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LegajoItem>> GetItemsAsync(Guid personLegajoId, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return await _dbContext.LegajoItems
            .AsNoTracking()
            .Where(i => i.TenantId == tenantId && i.PersonLegajoId == personLegajoId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LegajoIncome>> GetIncomesAsync(Guid personLegajoId, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return await _dbContext.LegajoIncomes
            .AsNoTracking()
            .Where(i => i.TenantId == tenantId && i.PersonLegajoId == personLegajoId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<LegajoItem?> GetItemAsync(Guid itemId, Guid personLegajoId, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return await _dbContext.LegajoItems
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.ItemId == itemId && i.PersonLegajoId == personLegajoId && i.TenantId == tenantId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> TryUpdateItemAsync(LegajoItem item, DateTime expectedUpdatedAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);

        var entry = _dbContext.LegajoItems.Attach(item);
        entry.Property(i => i.UpdatedAt).OriginalValue = expectedUpdatedAt;

        // Only the analyst columns: the document-derived ones belong to the rebuild.
        entry.Property(i => i.IsHousing).IsModified            = true;
        entry.Property(i => i.IsActivityAsset).IsModified      = true;
        entry.Property(i => i.OwnershipPct).IsModified         = true;
        entry.Property(i => i.InvestmentClass).IsModified      = true;
        entry.Property(i => i.AnalystNote).IsModified          = true;
        entry.Property(i => i.EditedFields).IsModified         = true;
        entry.Property(i => i.ReconciliationStatus).IsModified = true;
        entry.Property(i => i.ConfirmedFingerprint).IsModified = true;
        entry.Property(i => i.UpdatedAt).IsModified            = true;
        entry.Property(i => i.UpdatedBy).IsModified            = true;

        try
        {
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // The row moved (a rebuild, another edit) or is gone: the caller answers 409.
            return false;
        }
        finally
        {
            DetachOwnEntries();
        }
    }

    /// <inheritdoc />
    public async Task<LegajoReconciliationMergeResult> ApplyRebuildAsync(
        Guid personLegajoId, string tenantId, LegajoReconciliationBuild build, DateTime now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(build);

        try
        {
            var items = await _dbContext.LegajoItems
                .Where(i => i.TenantId == tenantId && i.PersonLegajoId == personLegajoId)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var incomes = await _dbContext.LegajoIncomes
                .Where(i => i.TenantId == tenantId && i.PersonLegajoId == personLegajoId)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var result = LegajoReconciliation.Merge(items, incomes, build, now);

            // Kept rows are modified in place; a removed key never reappears among the added ones (Merge pairs
            // by key), so no Deleted and Added command share a unique-index value in this SaveChanges.
            _dbContext.LegajoItems.RemoveRange(result.ItemsRemoved);
            _dbContext.LegajoItems.AddRange(result.ItemsAdded);
            _dbContext.LegajoIncomes.RemoveRange(result.IncomesRemoved);
            _dbContext.LegajoIncomes.AddRange(result.IncomesAdded);

            _dbContext.ChangeTracker.DetectChanges();
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            return result;
        }
        finally
        {
            DetachOwnEntries();
        }
    }

    /// <summary>
    /// Detaches every tracked item and income, whatever happened: the context is shared with the flow that
    /// triggered the write (an approval, the extraction worker), so only this repository's entities are
    /// released — never the caller's.
    /// </summary>
    private void DetachOwnEntries()
    {
        foreach (var entry in _dbContext.ChangeTracker.Entries()
                     .Where(e => e.Entity is LegajoItem or LegajoIncome)
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }
    }
}
