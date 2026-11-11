// Project : Api.Web.FinanceIA
// Created : 2026-10-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>Strength of a persona humana alert (M10 Phase 4a).</summary>
public static class PersonSeverities
{
    /// <summary>A plain alert.</summary>
    public const string Normal = "Normal";

    /// <summary>The design's "alerta fuerte": it does not suggest rejection, but it must be read before deciding.</summary>
    public const string Fuerte = "Fuerte";
}
