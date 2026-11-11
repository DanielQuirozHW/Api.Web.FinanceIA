// Project : Api.Web.FinanceIA
// Created : 2026-07-17

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Outcome of a soft-delete attempt, so the caller can distinguish 404 (not found)
/// from 403 (forbidden) from success.
/// </summary>
public enum SoftDeleteResult
{
    /// <summary>No note matched the id within the tenant.</summary>
    NotFound,

    /// <summary>The requester is neither the author nor an Admin — no mutation performed.</summary>
    Forbidden,

    /// <summary>The note is soft-deleted (either just now, or already was — idempotent).</summary>
    Success
}

/// <summary>
/// Persistence operations for PyME-level internal notes.
/// All reads are tenant-isolated; note content is append-only (never edited).
/// Notes may be soft-deleted: the row is retained and returned as a tombstone.
/// </summary>
public interface INoteRepository
{
    /// <summary>
    /// Retrieves all PyME-level notes for an entity, tenant-isolated and ordered by CreatedAt ASC
    /// (oldest first). Filters AnalysisId IS NULL to keep PyME-level notes separate from future
    /// analysis-level notes. Soft-deleted notes ARE included (returned as tombstones); the caller
    /// decides how to render them.
    /// </summary>
    Task<IReadOnlyList<Note>> GetByEntityAsync(Guid entityId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Appends a new note.
    /// </summary>
    Task CreateAsync(Note note, CancellationToken ct);

    /// <summary>
    /// Soft-deletes a note (tombstone): sets IsDeleted, DeletedAt and DeletedBy while retaining the row.
    /// Tenant-isolated. A non-Admin requester may only delete their own note; an Admin may delete any
    /// note in the tenant. Already-deleted notes are treated as success (idempotent).
    /// </summary>
    /// <returns>
    /// <see cref="SoftDeleteResult.NotFound"/> if no note matches, <see cref="SoftDeleteResult.Forbidden"/>
    /// if the requester lacks permission, otherwise <see cref="SoftDeleteResult.Success"/>.
    /// </returns>
    Task<SoftDeleteResult> SoftDeleteAsync(
        long noteId, string tenantId, Guid requestingUserId, bool isAdmin, CancellationToken ct);
}
