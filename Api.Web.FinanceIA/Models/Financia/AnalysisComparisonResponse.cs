// Project : Api.Web.FinanceIA
// Created : 2026-11-06

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// GET /api/analyses/{id}/previous-comparison — "Cambios respecto al análisis anterior": the most
/// important variables of this analysis against the previous decided analysis of the same entity and kind —
/// a company (Balance) analysis or a persona humana analysis.
/// Every verdict (delta, trend) is computed here so the Portal never computes one. Strings are codes
/// from <see cref="AnalysisComparisonCodes"/>.
/// </summary>
public sealed record AnalysisComparisonResponse
{
    /// <summary>False when there is nothing to compare; <see cref="Reason"/> then says why.</summary>
    public required bool Available { get; init; }

    /// <summary>One of <see cref="AnalysisComparisonCodes.Reasons"/> when not available; null otherwise.</summary>
    public string? Reason { get; init; }

    public AnalysisComparisonSide? Current { get; init; }

    public AnalysisComparisonSide? Previous { get; init; }

    /// <summary>
    /// Whether both company analyses cover the same fiscal year; null when not available and for a persona
    /// humana analysis, whose FiscalYear is only the year it was created.
    /// </summary>
    public bool? SameFiscalYear { get; init; }

    /// <summary><see cref="AnalysisComparisonCodes.Notes"/> codes; null when not available.</summary>
    public IReadOnlyList<string>? Notes { get; init; }

    /// <summary>The groups that had at least one readable row, in a fixed order; null when not available.</summary>
    public IReadOnlyList<AnalysisComparisonGroup>? Groups { get; init; }

    public static AnalysisComparisonResponse Unavailable(string reason) => new()
    {
        Available = false,
        Reason    = reason,
    };
}

/// <summary>The header of one side of the comparison.</summary>
public sealed record AnalysisComparisonSide
{
    public required Guid AnalysisId { get; init; }

    public required Guid ResultId { get; init; }

    public required int FiscalYear { get; init; }

    public required string Status { get; init; }

    /// <summary>ReviewedAt, else PreApprovedAt; null while the analysis is pending.</summary>
    public DateTime? DecidedAt { get; init; }

    /// <summary>The score on the displayed 0–1000 scale (<c>ScoreScale.ToNosis</c>); null for a persona humana analysis.</summary>
    public int? Score { get; init; }

    /// <summary>Low | Medium | High | Critical, as stored beside the score; null for a persona humana analysis.</summary>
    public string? Level { get; init; }

    public required string PromptVersion { get; init; }

    /// <summary>
    /// Persona humana: the ISO 4217 currency of this side's terms, which the <c>amount</c> rows are in.
    /// Absent (not even null) on a company response, so its JSON is unchanged.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }
}

/// <summary>One group of rows.</summary>
public sealed record AnalysisComparisonGroup
{
    /// <summary>One of <see cref="AnalysisComparisonCodes.Groups"/>.</summary>
    public required string Key { get; init; }

    /// <summary>es-AR title.</summary>
    public required string Label { get; init; }

    public required IReadOnlyList<AnalysisComparisonRow> Rows { get; init; }
}

/// <summary>One compared variable.</summary>
public sealed record AnalysisComparisonRow
{
    public required string Key { get; init; }

    /// <summary>es-AR label.</summary>
    public required string Label { get; init; }

    /// <summary>One of <see cref="AnalysisComparisonCodes.Units"/>.</summary>
    public required string Unit { get; init; }

    /// <summary>A number, or a string when <see cref="Unit"/> is "text"; null when unknown.</summary>
    public object? Current { get; init; }

    /// <summary>A number, or a string when <see cref="Unit"/> is "text"; null when unknown.</summary>
    public object? Previous { get; init; }

    /// <summary>Current − previous; null when either side is null or the unit is text.</summary>
    public decimal? Delta { get; init; }

    /// <summary>
    /// Delta / previous × 100, one decimal; null when either side is null, when previous ≤ 0, when the two sides
    /// have opposite signs, and for categories and counts that carry no percentage.
    /// </summary>
    public decimal? DeltaPct { get; init; }

    /// <summary>One of <see cref="AnalysisComparisonCodes.Directions"/>.</summary>
    public required string Direction { get; init; }

    /// <summary>One of <see cref="AnalysisComparisonCodes.Trends"/>.</summary>
    public required string Trend { get; init; }
}
