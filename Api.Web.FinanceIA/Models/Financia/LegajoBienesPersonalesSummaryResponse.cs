// Project : Api.Web.FinanceIA
// Created : 2026-11-06

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// LegajoExtractionViewResponse.BienesPersonalesSummary: a Bienes Personales extraction grouped by rubro,
/// with its totals printed and computed. Computed at read time from the EFFECTIVE values (after
/// corrections), never stored, never fed into an integrity check. Every amount is in
/// <see cref="AmountsUnit"/>, as printed — not scaled to pesos. An arithmetic overflow yields null.
/// PERSONAL DATA, like the view that carries it.
/// </summary>
public sealed record LegajoBienesPersonalesSummaryResponse
{
    /// <summary>Pesos, Miles or Millones: the unit of every amount below (the view's AmountsUnit).</summary>
    public required string AmountsUnit { get; init; }

    /// <summary>One group per rubro with at least one item, in LegajoBienesPersonalesSummary.RubroDisplayOrder.</summary>
    public required IReadOnlyList<LegajoBienesPersonalesRubroGroupResponse> Groups { get; init; }

    /// <summary>The document's totals, printed and computed.</summary>
    public required LegajoBienesPersonalesTotalsResponse Totals { get; init; }
}
