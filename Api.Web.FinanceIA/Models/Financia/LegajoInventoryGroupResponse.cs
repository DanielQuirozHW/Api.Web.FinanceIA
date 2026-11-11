// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>The items of one side and kind, with their totals computed at read time (pesos).</summary>
public sealed record LegajoInventoryGroupResponse
{
    /// <summary>Activo or Pasivo.</summary>
    public required string Side { get; init; }

    /// <summary>One of LegajoItemKinds.</summary>
    public required string Kind { get; init; }

    /// <summary>Sum of the market values the group's items carry.</summary>
    public required decimal MarketValueTotal { get; init; }

    /// <summary>Sum of the fiscal values the group's items carry.</summary>
    public required decimal FiscalValueTotal { get; init; }

    /// <summary>The items, in description order.</summary>
    public required IReadOnlyList<LegajoItemResponse> Items { get; init; }
}
