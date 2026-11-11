// Project : Api.Web.FinanceIA
// Created : 2026-07-17

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Response DTO for a single PyME-level note.
/// Returned by GET and POST /api/entities/{id}/notes.
/// When <see cref="IsDeleted"/> is true the note is a tombstone: <see cref="Content"/> is blanked
/// and the client renders "Nota de {AuthorName} eliminada" using the retained author and timestamp.
/// </summary>
public sealed class NoteResponse
{
    public long NoteId { get; set; }
    public Guid EntityId { get; set; }
    public string Content { get; set; } = string.Empty;
    public Guid AuthorUserId { get; set; }
    public string AuthorName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
}
