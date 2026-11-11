// Project : Api.Web.FinanceIA
// Created : 2026-11-02

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// What the score-reliability verdict (score credibility 1b-2b) needs of a dashboard row beyond the shared
/// <see cref="DashboardAnalysisRow"/> projection. Read in ONE page-sized query, only for the rows a section
/// actually shows, so the shared projection — which feeds uncapped sets such as the whole PreApproved queue —
/// never carries the breakdown blob.
/// </summary>
/// <param name="ResultId">AnalysisResults.ResultId.</param>
/// <param name="RescoredAt">AnalysisResults.RescoredAt; with GeneratedAt, the score's date.</param>
/// <param name="FiscalYear">Analyses.FiscalYear.</param>
/// <param name="ScoreBreakdownJson">AnalysisResults.ScoreBreakdownJson, whole — the adapter parses it as the detail does.</param>
public sealed record DashboardScoreReliabilitySource(
    Guid      ResultId,
    DateTime? RescoredAt,
    int       FiscalYear,
    string?   ScoreBreakdownJson);
