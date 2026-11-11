// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// What PATCH /api/analyses/{id}/decision requires before it accepts an Approved or a Rejected on this analysis
/// (score credibility 1b-5), derived from the same score-reliability verdict the detail shows. Null on a
/// persona humana analysis, which is never gated. A client that finds it ABSENT (an older BFF) requires
/// nothing — which is what makes shipping the Portal first safe.
/// </summary>
public sealed record DecisionRequirementsResponse
{
    /// <summary>
    /// True when the verdict is <c>provisional</c> or <c>none</c>: an APPROVAL (one-step, pre-approval or a
    /// confirmer's vote) must carry <c>acknowledgedCaveats: true</c> (409 <c>caveats_not_acknowledged</c>
    /// otherwise). A rejection never needs it, and <c>with_caveats</c> needs nothing.
    /// </summary>
    [JsonPropertyName("acknowledgeCaveats")]
    public required bool AcknowledgeCaveats { get; init; }

    /// <summary>
    /// True when the verdict raises <c>integrity_failed</c>: an approval must also carry a
    /// <c>figuresReviewNote</c> of at least 20 characters (409 <c>figures_review_required</c> otherwise), unless
    /// the figures are first fixed with POST /balance-corrections, which re-evaluates integrity.
    /// </summary>
    [JsonPropertyName("figuresReviewNote")]
    public required bool FiguresReviewNote { get; init; }

    /// <summary>
    /// True when the verdict raises <c>integrity_failed</c>: a rejection must say why, in at least 20 characters,
    /// in its <c>figuresReviewNote</c> or — when that is empty — in its <c>reviewNotes</c> (409
    /// <c>figures_review_required</c> otherwise).
    /// </summary>
    [JsonPropertyName("rejectionReasonRequired")]
    public required bool RejectionReasonRequired { get; init; }
}
