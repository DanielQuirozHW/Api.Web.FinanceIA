// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// The diff <see cref="LegajoReconciliation.Merge"/> computes against the stored rows. The existing rows
/// it keeps are modified IN PLACE (tracked entities); the lists name what to insert and delete.
/// </summary>
/// <param name="ItemsAdded">New items, ids and timestamps set.</param>
/// <param name="ItemsUpdated">How many kept items changed.</param>
/// <param name="ItemsRemoved">Stored items whose key the build no longer computes.</param>
/// <param name="ConfirmationsLapsed">AnalystConfirmed items whose conflict changed or vanished.</param>
/// <param name="IncomesAdded">New incomes.</param>
/// <param name="IncomesUpdated">How many kept incomes changed.</param>
/// <param name="IncomesRemoved">Stored incomes the build no longer computes.</param>
public sealed record LegajoReconciliationMergeResult(
    IReadOnlyList<LegajoItem> ItemsAdded,
    int ItemsUpdated,
    IReadOnlyList<LegajoItem> ItemsRemoved,
    int ConfirmationsLapsed,
    IReadOnlyList<LegajoIncome> IncomesAdded,
    int IncomesUpdated,
    IReadOnlyList<LegajoIncome> IncomesRemoved);
