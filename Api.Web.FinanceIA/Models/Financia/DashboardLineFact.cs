// Project : Api.Web.FinanceIA
// Created : 2026-10-23

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One cupo with the classification columns the portfolio dashboard groups by: its analysis (the latest
/// raise, else the origin), that analysis' kind and result, and the entity's sector. A repository
/// projection composed further in SQL — member-initialised so EF can keep translating after it.
/// </summary>
public sealed record DashboardLineFact
{
    /// <summary>CreditLines.CreditLineId.</summary>
    public Guid CreditLineId { get; init; }

    /// <summary>CreditLines.EntityId.</summary>
    public Guid EntityId { get; init; }

    /// <summary>CreditLines.Currency.</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>CreditLines.Amount.</summary>
    public decimal Amount { get; init; }

    /// <summary>COALESCE(LastRaiseAnalysisId, OriginAnalysisId); null on a legacy cupo.</summary>
    public Guid? AnalysisId { get; init; }

    /// <summary>Analyses.AnalysisKind of that analysis; null when it does not resolve in this tenant.</summary>
    public string? AnalysisKind { get; init; }

    /// <summary>AnalysisResults.ResultId of that analysis; null without a result.</summary>
    public Guid? ResultId { get; init; }

    /// <summary>AnalysisResults.AiSuggestedLevel of that analysis.</summary>
    public string? AiSuggestedLevel { get; init; }

    /// <summary>Entities.Sector, as stored.</summary>
    public string? Sector { get; init; }
}
