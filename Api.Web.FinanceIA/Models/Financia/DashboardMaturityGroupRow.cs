// Project : Api.Web.FinanceIA
// Created : 2026-10-23

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>Stored-Vigente operations of Active cupos sharing a currency and a ValidUntil date.</summary>
/// <param name="Currency">The cupo's currency.</param>
/// <param name="ValidUntil">The date part of ValidUntil.</param>
/// <param name="Count">How many operations.</param>
/// <param name="Amount">Their Amount.</param>
public sealed record DashboardMaturityGroupRow(string Currency, DateTime ValidUntil, int Count, decimal Amount);
