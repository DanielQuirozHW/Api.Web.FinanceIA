// Project : Api.Web.FinanceIA
// Created : 2026-10-30

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>Who decided <see cref="PersonAgroMode.Active"/>.</summary>
public static class PersonAgroModeOrigins
{
    /// <summary>Detected from the main ARCA activity (CLAE) and pf_agro_modo_anual.</summary>
    public const string Actividad = "actividad";

    /// <summary>Forced by the analyst on the request (agroMode true | false).</summary>
    public const string Analista = "analista";
}
