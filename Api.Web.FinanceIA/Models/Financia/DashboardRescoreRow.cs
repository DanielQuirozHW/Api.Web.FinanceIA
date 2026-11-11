// Project : Api.Web.FinanceIA
// Created : 2026-10-23

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One [dbo].[AnalysisRescores] row without its two nvarchar(max) columns — only the persona suggestions index of
/// PreviousValuesJson is read, server-side — with the entity and kind of its analysis.
/// </summary>
/// <param name="RescoreId">The row.</param>
/// <param name="AnalysisId">The analysis.</param>
/// <param name="ResultId">The result.</param>
/// <param name="RescoredAt">When.</param>
/// <param name="RescoredBy">Who.</param>
/// <param name="PreviousScore">Score before.</param>
/// <param name="NewScore">Score after.</param>
/// <param name="PreviousLevel">Level before.</param>
/// <param name="NewLevel">Level after.</param>
/// <param name="PreviousEnrichmentStatus">EnrichmentStatus before.</param>
/// <param name="NewEnrichmentStatus">EnrichmentStatus after.</param>
/// <param name="EntityId">Analyses.EntityId; null when the analysis is not this tenant's.</param>
/// <param name="AnalysisKind">Analyses.AnalysisKind; null when the analysis is not this tenant's.</param>
/// <param name="PreviousSuggestion">Persona humana: the suggestion before, read with JSON_VALUE; null on a balance row.</param>
/// <param name="NewSuggestion">Persona humana: the suggestion after; null on a balance row.</param>
public sealed record DashboardRescoreRow(
    Guid RescoreId, Guid AnalysisId, Guid ResultId, DateTime RescoredAt, Guid RescoredBy,
    decimal? PreviousScore, decimal? NewScore, string? PreviousLevel, string? NewLevel,
    string PreviousEnrichmentStatus, string NewEnrichmentStatus,
    Guid? EntityId, string? AnalysisKind,
    string? PreviousSuggestion = null, string? NewSuggestion = null);
