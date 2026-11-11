// Project : Api.Web.FinanceIA
// Created : 2026-10-30

namespace Api.Web.FinanceIA.Servicios.PersonEvaluation;

/// <summary>The monthly cuotas vigentes a debt-service ratio reads, and where they came from (engine 0.8).</summary>
/// <param name="Amount">Pesos a month: the declared figure when there is one, else the estimate.</param>
/// <param name="Estimate">The estimate from the Central de Deudores; null when the BCRA has no usable answer.</param>
/// <param name="Declared">The analyst declared <paramref name="Amount"/>.</param>
internal sealed record CuotasVigentes(decimal Amount, decimal? Estimate, bool Declared);
