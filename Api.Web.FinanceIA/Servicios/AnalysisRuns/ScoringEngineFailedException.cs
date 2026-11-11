// Project : Api.Web.FinanceIA
// Created : 2026-11-02

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>
/// Thrown by <see cref="AnalysisScoringStage.ScoreAsync"/> when the scoring engine returned its
/// neutral placeholder (<see cref="Models.Financia.ScoringResult.EngineFailed"/>) instead of a score.
/// Internal control flow only: the pipeline catches it and saves the analysis without a score; the
/// re-score catches it and refuses with 500 without writing. Never surfaced to a client.
/// </summary>
public sealed class ScoringEngineFailedException : Exception
{
    /// <summary>Initializes a new <see cref="ScoringEngineFailedException"/>.</summary>
    public ScoringEngineFailedException()
        : base("The scoring engine failed and returned its neutral placeholder; no score was produced.")
    {
    }
}
