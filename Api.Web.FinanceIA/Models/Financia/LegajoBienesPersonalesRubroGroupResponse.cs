// Project : Api.Web.FinanceIA
// Created : 2026-11-06

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The items of one rubro of a Bienes Personales extraction, with their fiscal-value sums and the rubro
/// subtotals the document prints. Amounts in the summary's AmountsUnit.
/// </summary>
public sealed record LegajoBienesPersonalesRubroGroupResponse
{
    /// <summary>One of LegajoExtractionVocabulary.Rubros.</summary>
    public required string Rubro { get; init; }

    /// <summary>JSON Pointers of the group's item rows (e.g. <c>/items/3</c>), in document order.</summary>
    public required IReadOnlyList<string> ItemPaths { get; init; }

    /// <summary>The number of item rows in the group.</summary>
    public required int ItemCount { get; init; }

    /// <summary>Sum of the rows' fiscalValue; null when no row carries one (or on overflow).</summary>
    public decimal? FiscalValueSum { get; init; }

    /// <summary>Sum of the fiscalValue of the rows located in the country; null when none carries one.</summary>
    public decimal? FiscalValueSumPais { get; init; }

    /// <summary>Sum of the fiscalValue of the rows located abroad; null when none carries one.</summary>
    public decimal? FiscalValueSumExterior { get; init; }

    /// <summary>Rows with no fiscalValue: left out of every sum.</summary>
    public required int ItemsWithoutFiscalValue { get; init; }

    /// <summary>The rubro subtotal printed for the country, or null when the document prints none.</summary>
    public decimal? PrintedSubtotalPais { get; init; }

    /// <summary>JSON Pointer of <see cref="PrintedSubtotalPais"/> (e.g. <c>/rubroTotals/0/total</c>), or null.</summary>
    public string? PrintedSubtotalPaisPath { get; init; }

    /// <summary>The rubro subtotal printed for abroad, or null when the document prints none.</summary>
    public decimal? PrintedSubtotalExterior { get; init; }

    /// <summary>JSON Pointer of <see cref="PrintedSubtotalExterior"/>, or null.</summary>
    public string? PrintedSubtotalExteriorPath { get; init; }
}
