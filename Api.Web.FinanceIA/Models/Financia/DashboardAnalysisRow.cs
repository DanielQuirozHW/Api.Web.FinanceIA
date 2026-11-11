// Project : Api.Web.FinanceIA
// Created : 2026-10-23

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One analysis joined to its result (1:1 by UQ_AnalysisResults_AnalysisId), projected to the columns
/// the dashboard reads and nothing else. No nvarchar(max) blob is selected — <see cref="HasBalance"/>
/// is a computed predicate over BalanceJson, and it is only computed by the reads that need it.
/// </summary>
public sealed record DashboardAnalysisRow
{
    /// <summary>AnalysisResults.ResultId.</summary>
    public Guid ResultId { get; init; }

    /// <summary>Analyses.AnalysisId.</summary>
    public Guid AnalysisId { get; init; }

    /// <summary>Analyses.AnalysisKind.</summary>
    public string AnalysisKind { get; init; } = AnalysisKinds.Balance;

    /// <summary>Analyses.EntityId.</summary>
    public Guid EntityId { get; init; }

    /// <summary>Analyses.DocumentId; null on a persona humana analysis.</summary>
    public Guid? DocumentId { get; init; }

    /// <summary>Analyses.RequestedBy.</summary>
    public Guid RequestedBy { get; init; }

    /// <summary>Analyses.RequestedAmount.</summary>
    public decimal? RequestedAmount { get; init; }

    /// <summary>Analyses.Status.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Analyses.CreatedAt.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Analyses.PreApprovedBy.</summary>
    public Guid? PreApprovedBy { get; init; }

    /// <summary>Analyses.PreApprovedAt.</summary>
    public DateTime? PreApprovedAt { get; init; }

    /// <summary>Analyses.PreApprovedAmount.</summary>
    public decimal? PreApprovedAmount { get; init; }

    /// <summary>Analyses.PreApprovedCurrency.</summary>
    public string? PreApprovedCurrency { get; init; }

    /// <summary>Analyses.ReviewedBy.</summary>
    public Guid? ReviewedBy { get; init; }

    /// <summary>Analyses.ReviewedAt.</summary>
    public DateTime? ReviewedAt { get; init; }

    /// <summary>Analyses.ReviewNotes — selected by the reads that show notes only, null elsewhere.</summary>
    public string? ReviewNotes { get; init; }

    /// <summary>Analyses.DecisionAmount.</summary>
    public decimal? DecisionAmount { get; init; }

    /// <summary>Analyses.DecisionCurrency.</summary>
    public string? DecisionCurrency { get; init; }

    /// <summary>AnalysisResults.GeneratedAt.</summary>
    public DateTime GeneratedAt { get; init; }

    /// <summary>AnalysisResults.AiSuggestedScore.</summary>
    public decimal? AiSuggestedScore { get; init; }

    /// <summary>AnalysisResults.AiSuggestedLevel.</summary>
    public string? AiSuggestedLevel { get; init; }

    /// <summary>AnalysisResults.EnrichmentStatus.</summary>
    public string EnrichmentStatus { get; init; } = string.Empty;

    /// <summary>
    /// AnalysisResults.BalanceJson is not null or whitespace — the detail's <c>hasBalance</c>. Computed
    /// only by the pending reads, which feed <see cref="RescoreEligibility"/>; false everywhere else.
    /// </summary>
    public bool HasBalance { get; init; }

    /// <summary>
    /// The row as the <see cref="Analysis"/> shape <see cref="RescoreEligibility.Evaluate"/> and the
    /// quorum engine read. Only the columns this row carries are set.
    /// </summary>
    public Analysis ToAnalysis() => new()
    {
        AnalysisId          = AnalysisId,
        AnalysisKind        = AnalysisKind,
        EntityId            = EntityId,
        DocumentId          = DocumentId,
        RequestedBy         = RequestedBy,
        RequestedAmount     = RequestedAmount,
        Status              = Status,
        CreatedAt           = CreatedAt,
        PreApprovedBy       = PreApprovedBy,
        PreApprovedAt       = PreApprovedAt,
        PreApprovedAmount   = PreApprovedAmount,
        PreApprovedCurrency = PreApprovedCurrency,
        ReviewedBy          = ReviewedBy,
        ReviewedAt          = ReviewedAt,
        ReviewNotes         = ReviewNotes,
        DecisionAmount      = DecisionAmount,
        DecisionCurrency    = DecisionCurrency
    };
}
