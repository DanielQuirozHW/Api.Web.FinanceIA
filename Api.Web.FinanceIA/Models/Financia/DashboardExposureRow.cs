// Project : Api.Web.FinanceIA
// Created : 2026-10-23

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>One Active cupo with its utilizado summed in SQL, for the top exposures and the high-utilization list.</summary>
/// <param name="CreditLineId">The cupo.</param>
/// <param name="EntityId">Its entity.</param>
/// <param name="Currency">Its currency.</param>
/// <param name="Amount">Its Amount (otorgado).</param>
/// <param name="Utilizado">Sum of Amount over its operations that occupy cupo.</param>
/// <param name="AnalysisId">COALESCE(LastRaiseAnalysisId, OriginAnalysisId).</param>
/// <param name="AnalysisKind">That analysis' kind; null when it does not resolve.</param>
/// <param name="ResultId">That analysis' ResultId; null without a result.</param>
/// <param name="AiSuggestedLevel">That result's level.</param>
public sealed record DashboardExposureRow(
    Guid CreditLineId, Guid EntityId, string Currency, decimal Amount, decimal Utilizado,
    Guid? AnalysisId, string? AnalysisKind, Guid? ResultId, string? AiSuggestedLevel);
