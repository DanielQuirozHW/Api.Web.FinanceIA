// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>Body of POST /api/documents/{id}/extraction/review.</summary>
public sealed record LegajoReviewRequest
{
    /// <summary>
    /// Optional — mandatory (at least five non-blank characters) when a check still fails or a required
    /// field is still missing on the effective values: the approval is then an explicit override.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Optional: the extraction the analyst reviewed (LegajoExtractionViewResponse.ExtractionId). When
    /// sent and no longer the current run, the approval is refused with 409 <c>extraction_changed</c>.
    /// </summary>
    public Guid? ExtractionId { get; init; }

    /// <summary>
    /// Optional: the corrections count the analyst reviewed (LegajoExtractionViewResponse.CorrectionsCount).
    /// When sent and different from the current count, the approval is refused with 409
    /// <c>extraction_changed</c>.
    /// </summary>
    public int? CorrectionsCount { get; init; }
}
