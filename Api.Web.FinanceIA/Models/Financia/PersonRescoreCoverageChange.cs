// Project : Api.Web.FinanceIA
// Created : 2026-10-30

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The fiadores' aggregate counter-guarantee coverage before and after a re-evaluation, both against the
/// tenant's CURRENT minimum — the one the decision applies. A null ratio means it cannot be computed (the
/// operation is not in pesos).
/// </summary>
public sealed record PersonRescoreCoverageChange(
    decimal? PreviousRatio,
    bool     PreviousMeetsMin,
    decimal? NewRatio,
    bool     NewMeetsMin);
