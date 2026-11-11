// Project : Api.Web.FinanceIA
// Created : 2026-10-23

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>How many analyses of a window are in one status, grouped in SQL.</summary>
/// <param name="Status">Analyses.Status.</param>
/// <param name="Count">How many.</param>
public sealed record DashboardStatusCountRow(string Status, int Count);
