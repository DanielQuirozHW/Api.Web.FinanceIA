// Project : Api.Web.FinanceIA
// Created : 2026-10-30

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The <c>errorCode</c> values a persona humana evaluation refuses with — on POST /api/person-analyses and,
/// for a re-evaluation, on POST /api/analyses/{id}/rescore. Part of the Portal contract: renaming one is a
/// breaking change.
/// </summary>
public static class PersonEvaluationErrorCodes
{
    public const string EntityNotFound       = "entity_not_found";
    public const string EntityNotPerson      = "entity_not_person";
    public const string ConsentRequired      = "consent_required";
    public const string LegajoNotReady       = "legajo_not_ready";
    public const string FiadorIsSolicitante  = "fiador_is_solicitante";
    public const string FiadorLegajoNotReady = "fiador_legajo_not_ready";

    /// <summary>The <c>reason</c> values of <see cref="FiadorLegajoNotReady"/>.</summary>
    public static class FiadorReasons
    {
        public const string NotFound        = "not_found";
        public const string NotActive       = "not_active";
        public const string NoLegajo        = "no_legajo";
        public const string ConsentRequired = "consent_required";
        public const string NoReadyDocument = "no_ready_document";

        /// <summary>Re-evaluation only: the fiador now links a different legajo than the one his row was evaluated on.</summary>
        public const string LegajoChanged   = "legajo_changed";
    }
}
