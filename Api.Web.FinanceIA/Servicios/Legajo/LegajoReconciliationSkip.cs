// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>An approved document that contributed nothing to a rebuild, and why (<see cref="LegajoReconciliationSkipReasons"/>).</summary>
/// <param name="DocumentId">The document.</param>
/// <param name="Reason">One of <see cref="LegajoReconciliationSkipReasons"/>.</param>
public sealed record LegajoReconciliationSkip(Guid DocumentId, string Reason);
