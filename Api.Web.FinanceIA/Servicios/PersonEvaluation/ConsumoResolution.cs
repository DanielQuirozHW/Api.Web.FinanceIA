// Project : Api.Web.FinanceIA
// Created : 2026-10-20

namespace Api.Web.FinanceIA.Servicios.PersonEvaluation;

/// <summary>The monthly consumo the flujo subtracts, and how it was reached.</summary>
/// <param name="Monthly">Pesos a month; null when there is nothing to use.</param>
/// <param name="DeclaredMonthly">The F.711 monto consumido / 12, when there is one.</param>
/// <param name="FloorApplied">The reference floor (pf_consumo_mensual_minimo_miles, CPI-indexed) stood in for or raised the declared figure.</param>
/// <param name="EvidenceKind">Verificado when it is the F.711 figure; Declarado when a parameter stands in.</param>
/// <param name="Basis">The basis, in es-AR, for the evidence.</param>
/// <param name="Floor">The floor this evaluation compared with, as <see cref="PersonEvaluationService.ResolveConsumoFloor"/> resolved it.</param>
/// <param name="DeclaredAnnual">The F.711 annual figure, when it is the one used (avoids re-annualising a rounded month).</param>
internal sealed record ConsumoResolution(
    decimal? Monthly, decimal? DeclaredMonthly, bool FloorApplied, string EvidenceKind, string Basis,
    ConsumoFloor Floor, decimal? DeclaredAnnual = null)
{
    /// <summary>The annual consumo the flujo subtracts.</summary>
    public decimal? Annual => DeclaredAnnual ?? Monthly * 12m;
}
