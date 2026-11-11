// Project : Api.Web.FinanceIA
// Created : 2026-10-30

namespace Api.Web.FinanceIA.Servicios.PersonEvaluation;

/// <summary>
/// The terms of the operation a persona humana analysis evaluates: what the request states on
/// POST /api/person-analyses, and what the solicitante's row froze when the analysis is re-evaluated.
/// </summary>
/// <param name="RequestedAmount">The requested amount (the solicitante's AmountAtCharge).</param>
/// <param name="Currency">ISO 4217 code of the amounts.</param>
/// <param name="InstallmentAmount">The cuota, when the analyst stated it.</param>
/// <param name="CuotasPorAnio">
/// How many cuotas a year the cuota is (1, 2, 4 or 12; engine 0.7). Null: not stated — on a new analysis the
/// tenant's pf_agro_cuotas_por_anio in agro mode, else 12; a row frozen before DDL 078 is re-evaluated with 12.
/// </param>
/// <param name="AgroModeOverride">The analyst's agro annual mode override (true | false); null: detected from ARCA.</param>
/// <param name="CuotasVigentesDeclaradas">
/// The solicitante's monthly cuotas vigentes as declared by the analyst (engine 0.8); null: estimated from the
/// Central de Deudores, as before — and how a row frozen before DDL 079 is re-evaluated.
/// </param>
public sealed record PersonOperationTerms(
    decimal RequestedAmount,
    string Currency,
    decimal? InstallmentAmount,
    int? CuotasPorAnio = null,
    bool? AgroModeOverride = null,
    decimal? CuotasVigentesDeclaradas = null);
