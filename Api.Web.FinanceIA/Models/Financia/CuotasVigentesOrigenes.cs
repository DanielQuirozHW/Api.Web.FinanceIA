// Project : Api.Web.FinanceIA
// Created : 2026-10-30

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Where the cuotas vigentes of a debt-service indicator came from — the <c>cuotasVigentesOrigen</c> input of
/// cuota_ingreso, cobertura_servicio_deuda and cuota_ingreso_estres (engine 0.8). Absent on earlier evaluations,
/// which always estimated.
/// </summary>
public static class CuotasVigentesOrigenes
{
    /// <summary>Declared by the analyst on the request (cuotasVigentesDeclaradas).</summary>
    public const string Declarada = "declarada";

    /// <summary>Estimated from the Central de Deudores balance (pf_cuota_estimada_pct_saldo).</summary>
    public const string Estimada = "estimada";
}
