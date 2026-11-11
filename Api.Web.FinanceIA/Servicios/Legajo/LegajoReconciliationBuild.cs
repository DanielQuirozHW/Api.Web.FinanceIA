// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// What <see cref="LegajoReconciliation.Build"/> derives from the approved documents: the items and
/// incomes as the documents state them (no analyst state, no ids, no timestamps), the documents that
/// contributed, and those that did not with the reason.
/// </summary>
/// <param name="Items">The inventory, document-derived columns only.</param>
/// <param name="Incomes">The incomes, one per source, concept and range.</param>
/// <param name="DocumentsUsed">The documents that contributed at least one row.</param>
/// <param name="Skipped">The approved documents that contributed none.</param>
public sealed record LegajoReconciliationBuild(
    IReadOnlyList<LegajoItem> Items,
    IReadOnlyList<LegajoIncome> Incomes,
    IReadOnlyList<Guid> DocumentsUsed,
    IReadOnlyList<LegajoReconciliationSkip> Skipped);
