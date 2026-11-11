// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// What C# concludes about one extraction tree (M10 Phase 2b) — the run's own reading in the pipeline,
/// or the effective values (corrections applied) in the review endpoints. Built by
/// <see cref="LegajoExtractionAssessor"/>.
/// </summary>
/// <param name="Checks">The integrity report over the tree.</param>
/// <param name="Missing">Required fields with no value (JSON Pointers).</param>
/// <param name="Flagged">Fields to look at, each with why.</param>
/// <param name="HolderCuit">A VALID holder CUIT the extraction read (XX-XXXXXXXX-X), or null. PII.</param>
/// <param name="HolderCuitMatches">True/false when a valid holder CUIT was read; null otherwise.</param>
/// <param name="MeanConfidence">Mean confidence of the fields and rows that hold a value, or null.</param>
public sealed record LegajoExtractionAssessment(
    LegajoIntegrityReport Checks,
    IReadOnlyList<string> Missing,
    IReadOnlyList<LegajoFlaggedField> Flagged,
    string? HolderCuit,
    bool? HolderCuitMatches,
    decimal? MeanConfidence)
{
    /// <summary>A flagged field's reason: its (or its row's) confidence is below Legajo:FieldConfidenceThreshold.</summary>
    public const string ReasonLowConfidence = "low_confidence";

    /// <summary>A flagged field's reason: it takes part in a failed integrity check.</summary>
    public const string ReasonCheckFailed = "check_failed";

    /// <summary>How many checks failed.</summary>
    public int FailedChecks => Checks.Checks.Count(c => !c.Passed);

    /// <summary>
    /// The document status this assessment implies: Blocked when the holder CUIT is another person's;
    /// Ready when nothing is missing, nothing is flagged and no check failed (a failed check without
    /// field paths — arithmetic_error — still needs review); NeedsReview otherwise.
    /// </summary>
    public string Status => HolderCuitMatches == false
        ? LegajoStatuses.Blocked
        : Missing.Count == 0 && Flagged.Count == 0 && FailedChecks == 0 ? LegajoStatuses.Ready : LegajoStatuses.NeedsReview;
}

/// <summary>One entry of the flagged review queue.</summary>
/// <param name="FieldPath">JSON Pointer of the field (or row).</param>
/// <param name="Reasons"><see cref="LegajoExtractionAssessment.ReasonLowConfidence"/> and/or <see cref="LegajoExtractionAssessment.ReasonCheckFailed"/>.</param>
public sealed record LegajoFlaggedField(string FieldPath, IReadOnlyList<string> Reasons);
