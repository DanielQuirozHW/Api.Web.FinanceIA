// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The inventory's totals, computed at read time (never stored), in pesos. A sum covers only the items
/// that carry that value; the counts say how many do not.
/// </summary>
public sealed record LegajoInventoryTotalsResponse
{
    /// <summary>Activo at market value (manifestación).</summary>
    public required decimal ActivoMarketValue { get; init; }

    /// <summary>Activo at fiscal value (Bienes Personales).</summary>
    public required decimal ActivoFiscalValue { get; init; }

    /// <summary>Pasivo (manifestación).</summary>
    public required decimal PasivoMarketValue { get; init; }

    /// <summary><see cref="ActivoMarketValue"/> − <see cref="PasivoMarketValue"/>.</summary>
    public required decimal PatrimonioNetoMarketValue { get; init; }

    /// <summary>Activo assigned to the person's activity, at market value.</summary>
    public required decimal ActivityAssetsMarketValue { get; init; }

    /// <summary>Activo items without a market value (listed in Bienes Personales only).</summary>
    public required int ActivoWithoutMarketValue { get; init; }

    /// <summary>Activo items without a fiscal value (listed in the manifestación only).</summary>
    public required int ActivoWithoutFiscalValue { get; init; }
}
