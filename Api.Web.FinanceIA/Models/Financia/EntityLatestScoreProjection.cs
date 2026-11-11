// Project : Api.Web.FinanceIA
// Created : 2026-11-04

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// An entity's most recent scored analysis result, as read by
/// <c>IAnalysisRepository.GetLatestScoreByEntityIdsAsync</c>: the score and the level persisted on
/// the SAME <see cref="AnalysisResult"/> row, so they can never disagree.
/// </summary>
/// <param name="EntityId">The entity the analysis belongs to.</param>
/// <param name="AnalysisId">The analysis whose result carries the score.</param>
/// <param name="Score">The stored <see cref="AnalysisResult.AiSuggestedScore"/> (0–100, motor scale).</param>
/// <param name="Level">The stored <see cref="AnalysisResult.AiSuggestedLevel"/>: Low / Medium / High / Critical.</param>
/// <param name="ScoredAt">When that score was produced: the last re-score, else the result's generation time (UTC).</param>
public sealed record EntityLatestScoreProjection(
    Guid     EntityId,
    Guid     AnalysisId,
    decimal  Score,
    string?  Level,
    DateTime ScoredAt)
{
    // The inputs of the score-reliability verdict (ScoreReliabilityAdapter), read from the SAME row as the
    // score, so the PyMEs list can say whether that score is provisional exactly as the analysis detail does.

    /// <summary>The <see cref="AnalysisResult.ResultId"/> of the row that carries the score.</summary>
    public Guid ResultId { get; init; }

    /// <summary><see cref="AnalysisResult.EnrichmentStatus"/> of that row.</summary>
    public string? EnrichmentStatus { get; init; }

    /// <summary><c>Analyses.AnalysisKind</c> of that analysis.</summary>
    public string? AnalysisKind { get; init; }

    /// <summary><c>Analyses.FiscalYear</c> of that analysis.</summary>
    public int? FiscalYear { get; init; }

    /// <summary><see cref="AnalysisResult.GeneratedAt"/> of that row (UTC).</summary>
    public DateTime GeneratedAt { get; init; }

    /// <summary><see cref="AnalysisResult.RescoredAt"/> of that row (UTC); null when never re-scored.</summary>
    public DateTime? RescoredAt { get; init; }

    /// <summary>
    /// <see cref="AnalysisResult.ScoreBreakdownJson"/> of that row — read in a second, page-sized query for
    /// the winning rows only, never for every scored analysis of the entity.
    /// </summary>
    public string? ScoreBreakdownJson { get; init; }
}
