// Project : Api.Web.FinanceIA
// Created : 2026-10-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One legajo document an evaluation read, pinned to the exact extraction run and the corrections applied
/// to it (M10 Phase 4a). The run alone is not enough: a correction changes the effective values without a
/// new run, so the correction ids are part of the identity.
/// </summary>
public sealed record PersonDocumentVersion
{
    /// <summary>The document.</summary>
    public required Guid DocumentId { get; init; }

    /// <summary>Its type.</summary>
    public required string DocumentType { get; init; }

    /// <summary>Its legajo status when read.</summary>
    public required string? LegajoStatus { get; init; }

    /// <summary>The period end it measures.</summary>
    public DateOnly? PeriodEnd { get; init; }

    /// <summary>The current extraction run read; null for a type with no extractor (e.g. the consent).</summary>
    public Guid? ExtractionId { get; init; }

    /// <summary>The run's schema version.</summary>
    public string? SchemaVersion { get; init; }

    /// <summary>The run's prompt version.</summary>
    public string? PromptVersion { get; init; }

    /// <summary>The model the run used.</summary>
    public string? Model { get; init; }

    /// <summary>SHA-256 of the run's output.</summary>
    public string? OutputHash { get; init; }

    /// <summary>The corrections applied, in the order they were applied.</summary>
    public IReadOnlyList<Guid> CorrectionIds { get; init; } = [];
}
