// Project : Api.Web.FinanceIA
// Created : 2026-07-17

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="INoteRepository"/>.
/// All reads use AsNoTracking; writes load a tracked entity and call DetectChanges before SaveChangesAsync.
/// </summary>
public sealed class NoteRepository : INoteRepository
{
    private readonly FinanciaDbContext          _dbContext;
    private readonly ILogger<NoteRepository>   _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="NoteRepository"/>.
    /// </summary>
    public NoteRepository(FinanciaDbContext dbContext, ILogger<NoteRepository> logger)
    {
        _dbContext = dbContext;
        _logger    = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Note>> GetByEntityAsync(
        Guid entityId, string tenantId, CancellationToken ct)
    {
        // Soft-deleted notes are intentionally included (tombstones); the response maps them
        // to an empty Content so their text never leaves the BFF.
        return await _dbContext.Notes
            .AsNoTracking()
            .Where(n => n.EntityId == entityId && n.TenantId == tenantId && n.AnalysisId == null)
            .OrderBy(n => n.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CreateAsync(Note note, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(note);

        _dbContext.Notes.Add(note);
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Created note {NoteId} for entity {EntityId} tenant {TenantId}",
            note.NoteId, note.EntityId, note.TenantId);
    }

    /// <inheritdoc />
    public async Task<SoftDeleteResult> SoftDeleteAsync(
        long noteId, string tenantId, Guid requestingUserId, bool isAdmin, CancellationToken ct)
    {
        // Tracked load (no AsNoTracking) so the mutation is picked up by DetectChanges.
        var note = await _dbContext.Notes
            .FirstOrDefaultAsync(n => n.NoteId == noteId && n.TenantId == tenantId, ct)
            .ConfigureAwait(false);

        if (note is null)
            return SoftDeleteResult.NotFound;

        if (!isAdmin && note.AuthorUserId != requestingUserId)
            return SoftDeleteResult.Forbidden;

        // Idempotent: an already-deleted note is a no-op success.
        if (note.IsDeleted)
            return SoftDeleteResult.Success;

        note.IsDeleted = true;
        note.DeletedAt = DateTime.UtcNow;
        note.DeletedBy = requestingUserId;
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Soft-deleted note {NoteId} for entity {EntityId} tenant {TenantId} by user {UserId}",
            note.NoteId, note.EntityId, tenantId, requestingUserId);

        return SoftDeleteResult.Success;
    }
}
