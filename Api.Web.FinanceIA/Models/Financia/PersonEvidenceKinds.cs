// Project : Api.Web.FinanceIA
// Created : 2026-10-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Whether a persona humana filter or indicator rests on verified or on declared sources (M10 Phase 4a).
/// Verified: tax returns filed with ARCA (DDJJ IVA, IIBB, Ganancias, monotributo, facturación), the BCRA,
/// ARCA and LUFE. Declared: what the person states to the creditor — the manifestación de bienes, the
/// certificación de ingresos, the projected cash flow — and any figure that falls back on a tenant
/// parameter. A mix of both is Declarado.
/// </summary>
public static class PersonEvidenceKinds
{
    /// <summary>Every input comes from a filed return or a registry.</summary>
    public const string Verificado = "Verificado";

    /// <summary>At least one input is the person's own statement (or a parameter standing in for one).</summary>
    public const string Declarado = "Declarado";

    /// <summary>Every kind.</summary>
    public static readonly IReadOnlyList<string> Known = [Verificado, Declarado];

    /// <summary>Verificado only when every part is; no part known reads as Declarado.</summary>
    public static string Combine(IEnumerable<string> parts)
    {
        var list = parts.ToList();
        return list.Count > 0 && list.All(p => p == Verificado) ? Verificado : Declarado;
    }

    /// <summary>The kind of a LegajoIncomes source.</summary>
    public static string OfIncomeSource(string? source) => source switch
    {
        LegajoIncomeSources.Iva or LegajoIncomeSources.Facturacion or LegajoIncomeSources.Iibb
            or LegajoIncomeSources.Ganancias or LegajoIncomeSources.Monotributo => Verificado,
        _ => Declarado
    };
}
