// Project : Api.Web.FinanceIA
// Created : 2026-10-30

namespace Api.Web.FinanceIA.Servicios.PersonEvaluation;

/// <summary>
/// The per-fiador inputs of an evaluation: what the request states on POST /api/person-analyses (with the
/// amount at charge already defaulted to the requested amount), and what the fiador's row froze when the
/// analysis is re-evaluated. The booleans keep their stored nullability; the evaluation reads them as
/// <c>== true</c>. <see cref="CuotasVigentesDeclaradas"/> is the fiador's own monthly cuotas vigentes as declared
/// by the analyst (engine 0.8); null estimates them from his Central de Deudores, as before.
/// </summary>
public sealed record PersonFiadorTerms(
    Guid     FiadorId,
    decimal  AmountAtCharge,
    string   GuaranteeType,
    bool?    SpouseConsent,
    bool?    HousingInopposable,
    decimal? GuaranteesGivenAmount,
    decimal? CuotasVigentesDeclaradas = null);
