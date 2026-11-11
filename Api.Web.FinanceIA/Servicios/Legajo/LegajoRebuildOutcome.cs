// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>What <see cref="ILegajoReconciliationService.RebuildAsync"/> did.</summary>
/// <param name="Result">The rebuild's counts; null when the legajo was not found or the race was lost.</param>
/// <param name="LegajoFound">False when the legajo is not in the tenant.</param>
/// <param name="LostRace">True when the rebuild lost twice to a concurrent write; nothing was saved.</param>
public sealed record LegajoRebuildOutcome(LegajoReconciliationResultResponse? Result, bool LegajoFound, bool LostRace);
