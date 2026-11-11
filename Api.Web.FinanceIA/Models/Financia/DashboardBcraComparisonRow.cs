// Project : Api.Web.FinanceIA
// Created : 2026-10-23

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One Active cupo with the two BCRA scalars of its entity's current snapshot and of its analysis' frozen
/// one — read with JSON_VALUE, so neither blob is selected.
/// </summary>
/// <param name="EntityId">The cupo's entity.</param>
/// <param name="CreditLineId">The cupo.</param>
/// <param name="NowEstadoRaw">JSON_VALUE(Entities.BcraDataJson, '$.deuda.estado').</param>
/// <param name="NowSituacionMaximaRaw">JSON_VALUE(Entities.BcraDataJson, '$.deuda.situacionMaxima').</param>
/// <param name="BcraDataUpdatedAt">Entities.BcraDataUpdatedAt.</param>
/// <param name="AnalysisId">The cupo's analysis; null on a legacy cupo.</param>
/// <param name="ResultId">That analysis' result; null without one.</param>
/// <param name="GeneratedAt">That result's GeneratedAt.</param>
/// <param name="ThenEstadoRaw">JSON_VALUE(AnalysisResults.BcraDataJson, '$.deuda.estado').</param>
/// <param name="ThenSituacionMaximaRaw">JSON_VALUE(AnalysisResults.BcraDataJson, '$.deuda.situacionMaxima').</param>
public sealed record DashboardBcraComparisonRow(
    Guid EntityId, Guid CreditLineId, string? NowEstadoRaw, string? NowSituacionMaximaRaw, DateTime? BcraDataUpdatedAt,
    Guid? AnalysisId, Guid? ResultId, DateTime? GeneratedAt, string? ThenEstadoRaw, string? ThenSituacionMaximaRaw);
