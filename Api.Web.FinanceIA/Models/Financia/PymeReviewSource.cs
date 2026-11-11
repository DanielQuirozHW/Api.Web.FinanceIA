// Project : Api.Web.FinanceIA
// Created : 2026-10-25

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One PyME with at least one Active cupo, and its most recent APPROVED analysis — the input of
/// PYME_REVIEW_DUE. Read by <c>CreditLineRepository.GetLatestApprovedAnalysesOfActiveEntitiesAsync</c>;
/// projected, no nvarchar(max) column is involved.
/// </summary>
public sealed record PymeReviewSource
{
    /// <summary>The PyME.</summary>
    public Guid EntityId { get; init; }

    /// <summary>Its latest analysis with Status 'Approved' (any AnalysisKind); null when it has none.</summary>
    public Guid? AnalysisId { get; init; }

    /// <summary>
    /// When that analysis was approved: ReviewedAt, or CreatedAt for a row without one. UTC. Null exactly
    /// when <see cref="AnalysisId"/> is.
    /// </summary>
    public DateTime? ApprovedAt { get; init; }
}
