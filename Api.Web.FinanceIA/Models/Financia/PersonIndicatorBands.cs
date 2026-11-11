// Project : Api.Web.FinanceIA
// Created : 2026-10-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>PROVISIONAL band of a persona humana indicator, where the design gives cuts (M10 Phase 4a).</summary>
public static class PersonIndicatorBands
{
    /// <summary>Inside the first cut.</summary>
    public const string Verde = "Verde";

    /// <summary>Between the first and the second cut.</summary>
    public const string Amarillo = "Amarillo";

    /// <summary>Between the second and the third cut.</summary>
    public const string Naranja = "Naranja";

    /// <summary>Beyond the last cut.</summary>
    public const string Rojo = "Rojo";
}
