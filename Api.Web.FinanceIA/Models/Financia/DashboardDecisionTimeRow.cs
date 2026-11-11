// Project : Api.Web.FinanceIA
// Created : 2026-10-23

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>When a decided analysis was created and decided.</summary>
/// <param name="CreatedAt">Analyses.CreatedAt.</param>
/// <param name="ReviewedAt">Analyses.ReviewedAt.</param>
public sealed record DashboardDecisionTimeRow(DateTime CreatedAt, DateTime ReviewedAt);
