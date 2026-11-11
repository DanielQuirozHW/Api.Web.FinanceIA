// Project : Api.Web.FinanceIA
// Created : 2026-08-16

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

// ── Interface ─────────────────────────────────────────────────────────────────

/// <summary>
/// Evaluates the per-tenant alert rule catalogue against an extracted balance, deterministically.
/// Same input → same output, always, including the order of the returned alerts.
///
/// This is the <c>alerts</c> channel. It exists because the model's own alert layer was measured
/// to be unstable across identical runs (the same finding under different code strings, and
/// findings oscillating between the alerts and anomalies channels) while everything downstream of
/// the extracted BalanceJson was already deterministic.
/// </summary>
public interface IAlertRuleEngine
{
    /// <summary>
    /// Evaluates every enabled rule and returns the alerts that fired, ordered by rule code.
    /// Never throws: a rule whose inputs are absent, or whose code the engine does not implement,
    /// is skipped rather than guessed at.
    /// </summary>
    /// <param name="rules">Catalogue rules for the tenant. Disabled rows are ignored.</param>
    /// <param name="accounts">Balance figures, or null when BalanceJson was absent or malformed.</param>
    /// <param name="cashFlow">
    /// Cash flow snapshot, or null when the blob has no <c>cash_flow</c> key at all (an analysis
    /// predating that feature) — distinct from a non-null snapshot with <c>Present == false</c>.
    /// </param>
    IReadOnlyList<CatalogueAlert> Evaluate(
        IReadOnlyList<AlertRuleDefinition> rules,
        BalanceAccounts?                   accounts,
        CashFlowData?                      cashFlow);
}
