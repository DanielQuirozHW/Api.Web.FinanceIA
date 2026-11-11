// Project : Api.Web.FinanceIA
// Created : 2026-10-30

namespace Api.Web.FinanceIA.Models.Financia;

// The narrow projections IPersonCalibrationRepository reads. Kept together, like the contract they feed.

/// <summary>One persona humana analysis of the calibration population: the columns the board reads, no more.</summary>
public sealed record PersonCalibrationAnalysisRow
{
    /// <summary>Analyses.AnalysisId.</summary>
    public required Guid AnalysisId { get; init; }

    /// <summary>Analyses.EntityId.</summary>
    public required Guid EntityId { get; init; }

    /// <summary>
    /// AnalysisResults.ResultId of this analysis — the id GET /api/analyses/{id} and the Portal's detail route
    /// take (UQ_AnalysisResults_AnalysisId: one row at most). Null when the analysis has no result row of this
    /// tenant.
    /// </summary>
    public Guid? ResultId { get; init; }

    /// <summary>Analyses.CreatedAt (UTC).</summary>
    public required DateTime CreatedAt { get; init; }

    /// <summary>Analyses.RequestedBy.</summary>
    public required Guid RequestedBy { get; init; }

    /// <summary>Analyses.Status.</summary>
    public required string Status { get; init; }

    /// <summary>Analyses.RequestedAmount.</summary>
    public decimal? RequestedAmount { get; init; }

    /// <summary>Analyses.PreApprovedBy.</summary>
    public Guid? PreApprovedBy { get; init; }

    /// <summary>Analyses.PreApprovedAt.</summary>
    public DateTime? PreApprovedAt { get; init; }

    /// <summary>Analyses.ReviewedBy.</summary>
    public Guid? ReviewedBy { get; init; }

    /// <summary>Analyses.ReviewedAt.</summary>
    public DateTime? ReviewedAt { get; init; }

    /// <summary>Analyses.ReviewNotes — free text; never logged.</summary>
    public string? ReviewNotes { get; init; }

    /// <summary>Analyses.PreApprovalChecksJson (AnalysisDecisionChecks).</summary>
    public string? PreApprovalChecksJson { get; init; }

    /// <summary>Analyses.ConfirmationChecksJson (AnalysisDecisionChecks).</summary>
    public string? ConfirmationChecksJson { get; init; }
}

/// <summary>One AnalysisRescores row, with the persona suggestions read server-side as GetHistoryAsync reads them.</summary>
public sealed record PersonCalibrationRescoreRow
{
    /// <summary>AnalysisRescores.RescoreId.</summary>
    public required Guid RescoreId { get; init; }

    /// <summary>AnalysisRescores.AnalysisId.</summary>
    public required Guid AnalysisId { get; init; }

    /// <summary>AnalysisRescores.ResultId — the result the re-evaluation overwrote in place.</summary>
    public required Guid ResultId { get; init; }

    /// <summary>AnalysisRescores.RescoredAt (UTC).</summary>
    public required DateTime RescoredAt { get; init; }

    /// <summary>AnalysisRescores.RescoredBy.</summary>
    public required Guid RescoredBy { get; init; }

    /// <summary>$.suggestions.previous of PreviousValuesJson.</summary>
    public string? PreviousSuggestion { get; init; }

    /// <summary>$.suggestions.new of PreviousValuesJson.</summary>
    public string? NewSuggestion { get; init; }
}

/// <summary>One pf_ ScoringConfig row of the tenant.</summary>
public sealed record PersonCalibrationConfigRow
{
    /// <summary>ScoringConfig.ConfigKey.</summary>
    public required string Key { get; init; }

    /// <summary>ScoringConfig.ConfigValue.</summary>
    public required decimal Value { get; init; }

    /// <summary>ScoringConfig.UpdatedAt (UTC).</summary>
    public required DateTime UpdatedAt { get; init; }
}

/// <summary>The tenant's persona humana history to date.</summary>
public sealed record PersonCalibrationMilestoneRow
{
    /// <summary>CreatedAt of the first persona analysis; null when there is none.</summary>
    public DateTime? FirstCreatedAt { get; init; }

    /// <summary>How many persona analyses the tenant has.</summary>
    public required int Count { get; init; }
}
