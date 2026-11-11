// Project : Api.Web.FinanceIA
// Created : 2026-10-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>Body of POST /api/person-analyses (M10 Phase 4a).</summary>
public sealed record CreatePersonAnalysisRequest
{
    /// <summary>The persona humana entity (PersonType Humana, linked to a legajo).</summary>
    public Guid EntityId { get; init; }

    /// <summary>The requested amount; must be &gt; 0.</summary>
    public decimal RequestedAmount { get; init; }

    /// <summary>ISO 4217 code; one of <c>Currencies.Known</c>.</summary>
    public string? Currency { get; init; }

    /// <summary>The cuota of the requested operation; optional, &gt; 0 when present.</summary>
    public decimal? InstallmentAmount { get; init; }

    /// <summary>
    /// How many cuotas a year the cuota is: 1 (annual, or a single payment at maturity), 2, 4 or 12; optional
    /// (engine 0.7). Without it: the tenant's pf_agro_cuotas_por_anio when the solicitante is evaluated in agro
    /// annual mode, else 12 — a monthly cuota, as before.
    /// </summary>
    public int? CuotasPorAnio { get; init; }

    /// <summary>
    /// The analyst's agro annual mode override: true forces it on, false forces it off, null (or absent) detects
    /// it from the main ARCA activity (CLAE 01, 02 or 03) and pf_agro_modo_anual.
    /// </summary>
    public bool? AgroMode { get; init; }

    /// <summary>
    /// The cuotas vigentes the solicitante already pays a month, declared by the analyst (pesos, &gt;= 0); optional
    /// (engine 0.8). When present it replaces the estimate from the Central de Deudores balance; zero declares
    /// that there are none, which is not the same as not stating it.
    /// </summary>
    public decimal? CuotasVigentesDeclaradas { get; init; }

    /// <summary>
    /// The fiadores to evaluate with the solicitante (M10 Phase 5a-2); optional. Without it, or empty, the
    /// analysis is the solicitante's alone, exactly as before.
    /// </summary>
    public IReadOnlyList<CreatePersonAnalysisFiadorRequest>? Fiadores { get; init; }
}
