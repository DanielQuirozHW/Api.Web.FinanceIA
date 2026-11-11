// Project : Api.Web.FinanceIA
// Created : 2026-10-29

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>One fiador of POST /api/person-analyses (M10 Phase 5a-2).</summary>
public sealed record CreatePersonAnalysisFiadorRequest
{
    /// <summary>An active fiador of the analysed entity, with a legajo.</summary>
    public Guid FiadorId { get; init; }

    /// <summary>The amount at the fiador's charge; &gt; 0 and not above the requested amount. Defaults to the requested amount.</summary>
    public decimal? AmountAtCharge { get; init; }

    /// <summary>One of <see cref="GuaranteeTypes.Known"/>; required.</summary>
    public string? GuaranteeType { get; init; }

    /// <summary>The spouse consented to execute the housing (asentimiento conyugal).</summary>
    public bool? SpouseConsent { get; init; }

    /// <summary>The housing is not protected against execution (inoponibilidad).</summary>
    public bool? HousingInopposable { get; init; }

    /// <summary>Guarantees the fiador already gave to others, declared by the analyst; &gt;= 0.</summary>
    public decimal? GuaranteesGivenAmount { get; init; }

    /// <summary>
    /// The cuotas vigentes the fiador already pays a month, declared by the analyst (pesos, &gt;= 0); optional
    /// (engine 0.8). When present it replaces the estimate in his stress cuota/ingreso.
    /// </summary>
    public decimal? CuotasVigentesDeclaradas { get; init; }
}
