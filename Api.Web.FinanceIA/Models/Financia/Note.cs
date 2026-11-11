// Project : Api.Web.FinanceIA
// Created : 2026-07-17

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[Notes].
/// Append-only PyME-level internal analyst notes (free-text commentary that lives
/// in no integrated data source).
/// Database First — schema changes via DDL scripts only.
/// </summary>
public sealed class Note
{
    /// <summary>Primary key (auto-increment).</summary>
    public long NoteId { get; set; }

    /// <summary>Tenant that owns this note.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Foreign key to [dbo].[Entities].EntityId — the PyME this note belongs to.</summary>
    public Guid EntityId { get; set; }

    /// <summary>Reserved for future analysis-level notes; always null for PyME-level notes.</summary>
    public Guid? AnalysisId { get; set; }

    /// <summary>Free-text note content.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Foreign key to [dbo].[Users].UserId — the author.</summary>
    public Guid AuthorUserId { get; set; }

    /// <summary>Denormalized snapshot of the author's display name at write time.</summary>
    public string AuthorName { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the note was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// True when the note has been soft-deleted. The row is retained (audit
    /// requirement) and surfaced to the UI as a tombstone; the content is never edited.
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <summary>UTC timestamp when the note was soft-deleted; null while live.</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>[dbo].[Users].UserId that soft-deleted the note; null while live. No FK by design.</summary>
    public Guid? DeletedBy { get; set; }
}
