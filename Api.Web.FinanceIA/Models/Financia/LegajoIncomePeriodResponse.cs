// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>One period (a month, a calendar year or another range) of GET /api/person-legajos/{id}/incomes.</summary>
public sealed record LegajoIncomePeriodResponse
{
    /// <summary>YYYY-MM, YYYY, or null for a range that is neither.</summary>
    public string? Period { get; init; }

    /// <summary>First day covered.</summary>
    public required DateOnly PeriodFrom { get; init; }

    /// <summary>Last day covered.</summary>
    public required DateOnly PeriodTo { get; init; }

    /// <summary>month, year or range.</summary>
    public required string Granularity { get; init; }

    /// <summary>Every figure of the period, by source then concept.</summary>
    public required IReadOnlyList<LegajoIncomeEntryResponse> Entries { get; init; }

    /// <summary>The sales coherence across sources.</summary>
    public required LegajoIncomeCoherenceResponse Coherence { get; init; }

    /// <summary>The source that prevails for sales: Iva, then Facturacion, Iibb, Certificacion, Manifestacion; null when none.</summary>
    public string? PrimarySalesSource { get; init; }

    /// <summary>The source that prevails for result and consumption: Ganancias, then Manifestacion; null when none.</summary>
    public string? PrimaryResultSource { get; init; }
}
