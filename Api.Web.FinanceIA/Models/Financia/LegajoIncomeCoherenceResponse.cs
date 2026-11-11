// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// How far apart the sources' sales figures of one period are, computed at read time over the
/// sales-like entries: Ventas of Iva, Facturacion, Iibb and Manifestacion, and the IngresoCertificado of
/// a certificación.
/// </summary>
public sealed record LegajoIncomeCoherenceResponse
{
    /// <summary>How many sources state a sales figure for the period.</summary>
    public required int Sources { get; init; }

    /// <summary>(max − min) / max over those figures, 0..1 (4 decimals); null with fewer than two sources or a max of zero or less.</summary>
    public decimal? MaxRelativeDiff { get; init; }

    /// <summary>The source with the highest figure, or null.</summary>
    public string? HighSource { get; init; }

    /// <summary>The source with the lowest figure, or null.</summary>
    public string? LowSource { get; init; }
}
