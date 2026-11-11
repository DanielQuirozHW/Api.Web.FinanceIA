// Project : Api.Web.FinanceIA
// Created : 2026-10-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>Whether a persona humana indicator could be computed (M10 Phase 4a).</summary>
public static class PersonIndicatorStatuses
{
    /// <summary>Computed; <c>Value</c> is set.</summary>
    public const string Ok = "Ok";

    /// <summary>An input is missing; <c>Value</c> is null and <c>Evidence</c> says which.</summary>
    public const string SinDatos = "SinDatos";
}
