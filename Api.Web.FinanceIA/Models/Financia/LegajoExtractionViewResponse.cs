// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// GET /api/documents/{id}/extraction (M10 Phase 2b): the current extraction's EFFECTIVE view — every
/// field with its provenance and correction history, the checks recomputed on the effective values, and
/// the two review queues. PERSONAL DATA: returned to the tenant's users only, never logged. It never
/// carries the holder CUIT the extraction read, only <see cref="HolderCuitMatches"/>.
/// </summary>
public sealed record LegajoExtractionViewResponse
{
    /// <summary>The current run.</summary>
    public required Guid ExtractionId { get; init; }

    /// <summary>The document.</summary>
    public required Guid DocumentId { get; init; }

    /// <summary>One of the types with an extractor (LegajoExtractionPrompts.SupportedTypes): BienesPersonales, ManifestacionBienes, Ganancias, DdjjIva, DdjjIibb, CertificacionIngresos or Monotributo.</summary>
    public required string DocumentType { get; init; }

    /// <summary>The document's pipeline status (NeedsReview, Ready, Blocked …).</summary>
    public string? LegajoStatus { get; init; }

    /// <summary>The output shape version.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>The prompt version.</summary>
    public required string PromptVersion { get; init; }

    /// <summary>The model id that read it.</summary>
    public required string Model { get; init; }

    /// <summary>Document or Images.</summary>
    public required string ReadMode { get; init; }

    /// <summary>UTC.</summary>
    public required DateTime CreatedAt { get; init; }

    /// <summary>When an analyst approved it, or null.</summary>
    public DateTime? ReviewedAt { get; init; }

    /// <summary>Who approved it (a user of this tenant), or null.</summary>
    public string? ReviewedByName { get; init; }

    /// <summary>
    /// True when the approval was an override (a check still failed or a required field was missing), false
    /// for a plain approval; null while not approved, and for a run approved before DDL 071 recorded it.
    /// </summary>
    public bool? ReviewedWithOverride { get; init; }

    /// <summary>The reason given on approval (mandatory for an override), or null.</summary>
    public string? ReviewReason { get; init; }

    /// <summary>Pesos, Miles or Millones: the unit the values below are printed in.</summary>
    public required string AmountsUnit { get; init; }

    /// <summary>The run's mean field confidence, 0..1.</summary>
    public decimal? Confidence { get; init; }

    /// <summary>Whether the holder CUIT the extraction read is the legajo's person; null when none valid was read.</summary>
    public bool? HolderCuitMatches { get; init; }

    /// <summary>Every field (provenance leaves and row values), effective values, in schema order.</summary>
    public required IReadOnlyList<LegajoExtractionFieldResponse> Fields { get; init; }

    /// <summary>The integrity checks recomputed on the effective values (amounts in pesos).</summary>
    public required LegajoIntegrityReport Checks { get; init; }

    /// <summary>Required fields with no value (JSON Pointers).</summary>
    public required IReadOnlyList<string> Missing { get; init; }

    /// <summary>Fields to look at, each with its reasons (low_confidence, check_failed).</summary>
    public required IReadOnlyList<LegajoFlaggedFieldResponse> Flagged { get; init; }

    /// <summary>Corrections recorded against the run.</summary>
    public required int CorrectionsCount { get; init; }

    /// <summary>
    /// Bienes Personales only (null for every other type, and when it cannot be computed): the items
    /// grouped by rubro and the totals printed and computed, from the effective values, in
    /// <see cref="AmountsUnit"/>. Read-time only; never stored, never fed into a check.
    /// </summary>
    public LegajoBienesPersonalesSummaryResponse? BienesPersonalesSummary { get; init; }
}
