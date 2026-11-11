// Project : Api.Web.FinanceIA
// Created : 2026-10-30

namespace Api.Web.FinanceIA.Servicios.PersonEvaluation;

/// <summary>
/// The consumo floor one evaluation compares with and subtracts: pf_consumo_mensual_minimo_miles, expressed in
/// pesos of pf_consumo_mes_base, re-expressed by CPI to the evaluation's reference month (engine 0.6).
/// </summary>
/// <param name="Base">The floor as configured, monthly pesos of <paramref name="BaseMonth"/> (0 = not defined).</param>
/// <param name="BaseMonth">The month the floor is expressed in.</param>
/// <param name="ReferenceMonth">The CPI reference month (YYYY-MM), or null without a series.</param>
/// <param name="Factor">The CPI factor applied, or null when the floor stays nominal.</param>
/// <param name="Monthly">The floor the evaluation uses: indexed when <paramref name="Factor"/> is set, else <paramref name="Base"/>.</param>
/// <param name="NominalNote">Why the floor stays nominal, in es-AR; null when it is indexed.</param>
internal sealed record ConsumoFloor(
    decimal Base, DateOnly BaseMonth, string? ReferenceMonth, decimal? Factor, decimal Monthly, string? NominalNote)
{
    /// <summary>True when the floor was re-expressed by CPI.</summary>
    public bool Indexed => Factor is not null;
}
