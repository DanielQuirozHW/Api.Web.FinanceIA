// Project : Api.Web.FinanceIA
// Created : 2026-10-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Annual gross-income ceiling of each monotributo category, in pesos — the cap the "ingreso declarado
/// por encima del tope" filter compares against (M10 Phase 4a).
/// </summary>
/// <remarks>
/// ⚠ CODE TABLE, UPDATED BY HAND. Values are ARCA's table in force from <see cref="VigenteDesde"/>,
/// copied from https://www.arca.gob.ar/monotributo/categorias.asp. ARCA updates the caps twice a year by
/// CPI (February and August), so this table goes stale. A STALE CAP IS NOT HARMLESS: with inflation the
/// real caps rise, so an old (lower) cap flags sales that are inside the category today as above it —
/// FALSE STRONG ALERTS on compliant monotributistas. Update it with every ARCA revision. It is a code
/// table on purpose rather than a tenant parameter — the caps are national, not a credit policy — and
/// the filter's evidence names the table and its date so the analyst can weigh it.
/// </remarks>
public static class MonotributoCategoryCaps
{
    /// <summary>First day the table applies.</summary>
    public static readonly DateOnly VigenteDesde = new(2026, 8, 1);

    /// <summary>Category letter (A–K) → annual gross-income cap in pesos.</summary>
    public static readonly IReadOnlyDictionary<string, decimal> AnnualCaps = new Dictionary<string, decimal>(StringComparer.Ordinal)
    {
        ["A"] =   12_009_410.45m,
        ["B"] =   17_595_182.74m,
        ["C"] =   24_670_494.31m,
        ["D"] =   30_628_651.43m,
        ["E"] =   36_028_231.33m,
        ["F"] =   45_151_659.41m,
        ["G"] =   53_995_798.87m,
        ["H"] =   81_924_660.37m,
        ["I"] =   91_699_761.90m,
        ["J"] =  105_012_519.20m,
        ["K"] =  126_610_838.75m,
    };
}
