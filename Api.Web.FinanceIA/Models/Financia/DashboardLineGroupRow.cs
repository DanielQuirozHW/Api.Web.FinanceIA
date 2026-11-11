// Project : Api.Web.FinanceIA
// Created : 2026-10-23

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One SQL group of cupos (or of their cupo-occupying operations) for the portfolio dashboard. Which key
/// columns are set depends on the read; the others are null.
/// </summary>
/// <param name="Currency">The cupo's currency — the only currency any amount here is in.</param>
/// <param name="Status">CreditLines.Status, for the totals read.</param>
/// <param name="AnalysisKind">The cupo analysis' kind, for the risk reads.</param>
/// <param name="AiSuggestedLevel">The cupo analysis' level, for the risk reads.</param>
/// <param name="Sector">Entities.Sector as stored, for the sector reads.</param>
/// <param name="Count">Rows in the group: cupos, or operations.</param>
/// <param name="EntityCount">Distinct entities in the group; 0 where the read does not count them.</param>
/// <param name="Amount">Sum of Amount over the group.</param>
public sealed record DashboardLineGroupRow(
    string Currency, string? Status, string? AnalysisKind, string? AiSuggestedLevel, string? Sector,
    int Count, int EntityCount, decimal Amount);
