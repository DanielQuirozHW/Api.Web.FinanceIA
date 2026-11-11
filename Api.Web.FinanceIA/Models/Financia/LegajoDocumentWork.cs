// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// What the legajo worker needs about one document and its legajo, in one tenant-scoped projection.
/// PII: <see cref="LegajoCuit"/> is used ONLY for the post-hoc comparison in C#; it is never logged
/// and never reaches a request.
/// </summary>
public sealed record LegajoDocumentWork
{
    /// <summary>The document.</summary>
    public required Guid DocumentId { get; init; }

    /// <summary>Tenant slug.</summary>
    public required string TenantId { get; init; }

    /// <summary>The legajo the document belongs to.</summary>
    public required Guid PersonLegajoId { get; init; }

    /// <summary>The type the analyst declared at upload.</summary>
    public required string DocumentType { get; init; }

    /// <summary>Where the bytes live (from the DB, never from a caller).</summary>
    public required string StoragePath { get; init; }

    /// <summary>For the per-file attempt budget.</summary>
    public required string FileHashSha256 { get; init; }

    /// <summary>Current Documents.LegajoStatus.</summary>
    public string? LegajoStatus { get; init; }

    /// <summary>Soft-deleted.</summary>
    public bool IsDeleted { get; init; }

    /// <summary>Documents.PeriodEnd: the classified or analyst-confirmed period close, or null.</summary>
    public DateTime? PeriodEnd { get; init; }

    /// <summary>The legajo's person CUIT (hyphenated). PII.</summary>
    public required string LegajoCuit { get; init; }

    /// <summary>
    /// True when the legajo has ACTIVE consent: recorded, pointing at an AutorizacionDatos document
    /// of the same legajo that still exists and is not deleted.
    /// </summary>
    public bool HasActiveConsent { get; init; }
}
