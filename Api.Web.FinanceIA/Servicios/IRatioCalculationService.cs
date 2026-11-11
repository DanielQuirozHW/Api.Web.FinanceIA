// Project : Api.Web.FinanceIA
// Created : 2026-06-10

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

// ── Interface ─────────────────────────────────────────────────────────────────

/// <summary>
/// Computes the standard set of financial ratios from raw balance-sheet and
/// income-statement figures deterministically (pure math, no I/O).
/// </summary>
public interface IRatioCalculationService
{
    /// <summary>
    /// Calculates financial ratios from the supplied account balances.
    /// Any ratio whose required inputs are null or would produce a
    /// division by zero is returned as <c>null</c>.
    /// </summary>
    /// <param name="accounts">Raw balance-sheet / income-statement figures.</param>
    /// <returns>A <see cref="RatiosData"/> instance with computed ratios.</returns>
    RatiosData Calculate(BalanceAccounts accounts);
}
