// Project : Api.Web.FinanceIA
// Created : 2026-10-23

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>The duration of one ApiCallLogs row that carries one.</summary>
/// <param name="Service">ApiCallLogs.Service.</param>
/// <param name="DurationMs">ApiCallLogs.DurationMs.</param>
public sealed record DashboardApiCallDurationRow(string Service, int DurationMs);
