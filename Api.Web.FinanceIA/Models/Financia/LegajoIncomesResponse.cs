// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// GET /api/person-legajos/{id}/incomes (M10 Phase 3b): the legajo's incomes and sales by period and
/// source, with a coherence summary per period computed at read time. Amounts in pesos.
/// </summary>
public sealed record LegajoIncomesResponse
{
    /// <summary>The legajo.</summary>
    public required Guid PersonLegajoId { get; init; }

    /// <summary>Newest period first (by its last day, then its first).</summary>
    public required IReadOnlyList<LegajoIncomePeriodResponse> Periods { get; init; }
}
