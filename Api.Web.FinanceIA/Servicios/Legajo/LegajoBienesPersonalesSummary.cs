// Project : Api.Web.FinanceIA
// Created : 2026-11-06

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using V = Api.Web.FinanceIA.Servicios.Legajo.LegajoExtractionVocabulary;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// The read-time summary of a Bienes Personales extraction (LegajoExtractionViewResponse.BienesPersonalesSummary):
/// the items grouped by rubro with their fiscal-value sums, the printed rubro subtotals, and the three asset
/// totals printed and computed. Pure; never stored; never fed into an integrity check — a computed total is
/// shown to the analyst, it does not make a check pass.
/// </summary>
/// <remarks>
/// <para>
/// Amounts stay in the document's printed unit (no scaling to pesos), so they read like the form.
/// </para>
/// <para>
/// Arithmetic is decimal. An overflow makes the affected sum null and never throws, as in
/// <see cref="LegajoIntegrityService"/>: a GET must never fail because of this summary.
/// </para>
/// <para>
/// A rubro printed only as a subtotal (no item rows — dinero en efectivo, typically) gets no group and adds
/// nothing to a computed total: computed figures are sums of items only, never mixed with printed subtotals.
/// </para>
/// </remarks>
public static class LegajoBienesPersonalesSummary
{
    /// <summary>A printed value exists.</summary>
    public const string SourcePrinted = "printed";

    /// <summary>Nothing printed, but a value could be computed.</summary>
    public const string SourceComputed = "computed";

    /// <summary>Neither printed nor computable.</summary>
    public const string SourceNone = "none";

    /// <summary>
    /// The order the groups are listed in. A permutation of <see cref="LegajoExtractionVocabulary.Rubros"/>, which cannot be reordered
    /// itself: it is the enum of the fingerprinted tool schema.
    /// </summary>
    public static readonly IReadOnlyList<string> RubroDisplayOrder =
    [
        "Inmuebles", "Rodados", "Depositos", "DineroEfectivo", "TitulosYAcciones", "Creditos",
        "BienesMueblesRegistrables", "OtrosBienes", "Otro"
    ];

    /// <summary>The catch-all rubro a row with an unknown rubro is grouped under, so no row is dropped.</summary>
    private const string FallbackRubro = "Otro";

    private const string ItemsPath       = "/items";
    private const string RubroTotalsPath = "/rubroTotals";

    /// <summary>Builds the summary of an effective Bienes Personales tree.</summary>
    /// <param name="effective">The effective ResultJson tree (after corrections).</param>
    /// <param name="amountsUnit">The unit the document prints (the view's AmountsUnit).</param>
    public static LegajoBienesPersonalesSummaryResponse Build(JsonObject effective, string amountsUnit)
    {
        ArgumentNullException.ThrowIfNull(effective);
        ArgumentException.ThrowIfNullOrWhiteSpace(amountsUnit);

        var items       = Rows(effective, ItemsPath);
        var rubroTotals = Rows(effective, RubroTotalsPath);

        var built = items
            .GroupBy(i => GroupRubro(LegajoJsonValues.String(i.Node["rubro"])))
            .OrderBy(g => RubroRank(g.Key))
            .Select(g => BuildGroup(g.Key, g.ToList(), rubroTotals))
            .ToList();
        var groups = built.Select(b => b.Response).ToList();

        var printedPais     = LegajoJsonValues.FieldDecimal(effective, "/totals/bienesPais");
        var printedExterior = LegajoJsonValues.FieldDecimal(effective, "/totals/bienesExterior");
        var printedTotal    = LegajoJsonValues.FieldDecimal(effective, "/totals/totalBienes");

        // A group whose sum overflowed makes the computed total unknown, never a silently partial sum.
        var computedPais     = built.Any(b => b.PaisOverflowed) ? null : SumAll(groups.Select(g => g.FiscalValueSumPais));
        var computedExterior = built.Any(b => b.ExteriorOverflowed) ? null : SumAll(groups.Select(g => g.FiscalValueSumExterior));
        var computedTotal    = printedPais is not null && printedExterior is not null
            ? Add(printedPais, printedExterior)
            : built.Any(b => b.SumOverflowed) ? null : SumAll(groups.Select(g => g.FiscalValueSum));

        return new LegajoBienesPersonalesSummaryResponse
        {
            AmountsUnit = amountsUnit,
            Groups      = groups,
            Totals      = new LegajoBienesPersonalesTotalsResponse
            {
                BienesPais          = Total("/totals/bienesPais", printedPais, computedPais),
                BienesExterior      = Total("/totals/bienesExterior", printedExterior, computedExterior),
                TotalBienes         = Total("/totals/totalBienes", printedTotal, computedTotal),
                BienesExentos       = LegajoJsonValues.FieldDecimal(effective, "/totals/bienesExentos"),
                MinimoNoImponible   = LegajoJsonValues.FieldDecimal(effective, "/totals/minimoNoImponible"),
                BaseImponible       = LegajoJsonValues.FieldDecimal(effective, "/totals/baseImponible"),
                ImpuestoDeterminado = LegajoJsonValues.FieldDecimal(effective, "/totals/impuestoDeterminado")
            }
        };
    }

    // ── Private helpers ──────────────────────────────────────────────────────────

    private sealed record Row(string Path, JsonObject Node);

    private sealed record BuiltGroup(
        LegajoBienesPersonalesRubroGroupResponse Response, bool SumOverflowed, bool PaisOverflowed, bool ExteriorOverflowed);

    private static List<Row> Rows(JsonObject root, string path)
        => LegajoJsonValues.ArrayAt(root, path)
            .Select((node, index) => (Path: LegajoJsonPointer.Child(path, index), Node: node as JsonObject))
            .Where(r => r.Node is not null)
            .Select(r => new Row(r.Path, r.Node!))
            .ToList();

    private static string GroupRubro(string? rubro)
        => rubro is not null && RubroDisplayOrder.Contains(rubro) ? rubro : FallbackRubro;

    private static int RubroRank(string rubro)
    {
        for (var i = 0; i < RubroDisplayOrder.Count; i++)
        {
            if (RubroDisplayOrder[i] == rubro)
                return i;
        }

        return RubroDisplayOrder.Count;
    }

    private static BuiltGroup BuildGroup(
        string rubro, IReadOnlyList<Row> members, IReadOnlyList<Row> rubroTotals)
    {
        decimal? sum = null, pais = null, exterior = null;
        var withoutValue = 0;
        bool sumOverflowed = false, paisOverflowed = false, exteriorOverflowed = false;

        foreach (var item in members)
        {
            if (LegajoJsonValues.Decimal(item.Node["fiscalValue"]) is not { } value)
            {
                withoutValue++;
                continue;
            }

            Accumulate(ref sum, ref sumOverflowed, value);

            var location = LegajoJsonValues.String(item.Node["location"]);
            if (location == V.Pais)
                Accumulate(ref pais, ref paisOverflowed, value);
            else if (location == V.Exterior)
                Accumulate(ref exterior, ref exteriorOverflowed, value);
        }

        var (subtotalPais, subtotalPaisPath)         = PrintedSubtotal(rubroTotals, rubro, V.Pais);
        var (subtotalExterior, subtotalExteriorPath) = PrintedSubtotal(rubroTotals, rubro, V.Exterior);

        var response = new LegajoBienesPersonalesRubroGroupResponse
        {
            Rubro                       = rubro,
            ItemPaths                   = members.Select(m => m.Path).ToList(),
            ItemCount                   = members.Count,
            FiscalValueSum              = sumOverflowed ? null : sum,
            FiscalValueSumPais          = paisOverflowed ? null : pais,
            FiscalValueSumExterior      = exteriorOverflowed ? null : exterior,
            ItemsWithoutFiscalValue     = withoutValue,
            PrintedSubtotalPais         = subtotalPais,
            PrintedSubtotalPaisPath     = subtotalPaisPath,
            PrintedSubtotalExterior     = subtotalExterior,
            PrintedSubtotalExteriorPath = subtotalExteriorPath
        };

        return new BuiltGroup(response, sumOverflowed, paisOverflowed, exteriorOverflowed);
    }

    // The first rubroTotals row, in document order, printed for this rubro and location.
    private static (decimal? Total, string? Path) PrintedSubtotal(IReadOnlyList<Row> rubroTotals, string rubro, string location)
    {
        var row = rubroTotals.FirstOrDefault(t => GroupRubro(LegajoJsonValues.String(t.Node["rubro"])) == rubro
                                               && LegajoJsonValues.String(t.Node["location"]) == location);
        return row is null ? (null, null) : (LegajoJsonValues.Decimal(row.Node["total"]), row.Path + "/total");
    }

    // Once a running sum overflowed it stays unknown: a later addition never revives it.
    private static void Accumulate(ref decimal? sum, ref bool overflowed, decimal value)
    {
        if (overflowed)
            return;

        var next = Add(sum ?? 0m, value);
        if (next is null)
            overflowed = true;
        else
            sum = next;
    }

    // Sum of the non-null values; null when there are none, or when the sum overflows.
    private static decimal? SumAll(IEnumerable<decimal?> values)
    {
        decimal? sum = null;
        foreach (var value in values)
        {
            if (value is not { } v)
                continue;

            sum = Add(sum ?? 0m, v);
            if (sum is null)
                return null;
        }

        return sum;
    }

    private static decimal? Add(decimal? x, decimal? y)
    {
        if (x is not { } a || y is not { } b)
            return null;

        try
        {
            return a + b;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static LegajoBienesPersonalesTotalResponse Total(string path, decimal? printed, decimal? computed) => new()
    {
        FieldPath = path,
        Printed   = printed,
        Computed  = computed,
        Source    = printed is not null ? SourcePrinted : computed is not null ? SourceComputed : SourceNone
    };
}
