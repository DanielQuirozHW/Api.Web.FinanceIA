// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[LegajoExtractions] (DDL 070): ONE immutable row per model run
/// over a legajo document — the classification pass in Phase 2a, the extraction pass in 2b.
///
/// <para>
/// Immutable except for two columns: <see cref="IsCurrent"/> (swapped off when a newer run of the
/// same <see cref="Kind"/> lands, in the same SaveChanges that inserts it — UX_LegajoExtractions_Current
/// admits one current run per (DocumentId, Kind)), and the Phase 2b review stamp
/// (<see cref="ReviewedBy"/>/<see cref="ReviewedAt"/>, and since DDL 071 <see cref="ReviewOverride"/>/
/// <see cref="ReviewReason"/>). Corrections never edit
/// <see cref="ResultJson"/>; they are rows of [dbo].[LegajoCorrections].
/// </para>
///
/// <para>
/// PII: <see cref="ResultJson"/> is what the model read from the person's paperwork, and may hold a
/// CUIT the model quoted. It is never logged, never returned wholesale, and never fed back into a
/// prompt.
/// </para>
/// </summary>
public sealed class LegajoExtraction
{
    /// <summary>Primary key. Supplied by the application; the NEWSEQUENTIALID() default serves hand-written rows.</summary>
    public Guid ExtractionId { get; set; }

    /// <summary>Tenant that owns the run. Firebase tenant slug.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The legajo the document belongs to.</summary>
    public Guid PersonLegajoId { get; set; }

    /// <summary>The document the run read.</summary>
    public Guid DocumentId { get; set; }

    /// <summary>One of <see cref="LegajoExtractionKinds.Known"/>.</summary>
    public string Kind { get; set; } = LegajoExtractionKinds.Classification;

    /// <summary>
    /// The document type the run worked with: for a classification, the type the model DETECTED
    /// (or "Desconocido"); for an extraction, the type the schema was chosen by.
    /// </summary>
    public string DocumentType { get; set; } = string.Empty;

    /// <summary>Version of the output schema (the tool's input_schema).</summary>
    public string SchemaVersion { get; set; } = string.Empty;

    /// <summary>Version of the prompt, pinned by a fingerprint test.</summary>
    public string PromptVersion { get; set; } = string.Empty;

    /// <summary>Exact model id sent on the wire.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>One of <see cref="LegajoReadModes.Known"/>.</summary>
    public string ReadMode { get; set; } = LegajoReadModes.Document;

    /// <summary>One of <see cref="LegajoExtractionStatuses.Known"/>.</summary>
    public string Status { get; set; } = LegajoExtractionStatuses.Succeeded;

    /// <summary>The run's result as JSON. Each field carries its own provenance (page, quote).</summary>
    public string ResultJson { get; set; } = "{}";

    /// <summary>Phase 2b: the arithmetic checks over the result. Null for a classification.</summary>
    public string? ChecksJson { get; set; }

    /// <summary>The model's self-reported confidence, 0..1, or null when the run failed.</summary>
    public decimal? Confidence { get; set; }

    /// <summary>Input tokens billed.</summary>
    public int InputTokens { get; set; }

    /// <summary>Output tokens billed.</summary>
    public int OutputTokens { get; set; }

    /// <summary>Estimated USD cost, or null when the model has no price entry.</summary>
    public decimal? CostUsd { get; set; }

    /// <summary>SHA-256 hex of the serialized request body.</summary>
    public string InputHash { get; set; } = string.Empty;

    /// <summary>SHA-256 hex of the raw response body (empty when there was none).</summary>
    public string OutputHash { get; set; } = string.Empty;

    /// <summary>True for the latest run of this <see cref="Kind"/> over this document.</summary>
    public bool IsCurrent { get; set; }

    /// <summary>UTC. Set explicitly by the application (the in-memory provider ignores SQL defaults).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UserId that triggered the run, or null for the background worker.</summary>
    public Guid? CreatedBy { get; set; }

    /// <summary>Phase 2b: the analyst who reviewed the run.</summary>
    public Guid? ReviewedBy { get; set; }

    /// <summary>Phase 2b: when the run was reviewed.</summary>
    public DateTime? ReviewedAt { get; set; }

    /// <summary>
    /// DDL 071: true when the approval was an override (a check still failed or a required field was
    /// still missing on the effective values), false for a plain approval, null while not approved.
    /// Set and cleared together with <see cref="ReviewedAt"/>.
    /// </summary>
    public bool? ReviewOverride { get; set; }

    /// <summary>DDL 071: the reason the reviewer gave on approval (mandatory for an override), or null.</summary>
    public string? ReviewReason { get; set; }
}
