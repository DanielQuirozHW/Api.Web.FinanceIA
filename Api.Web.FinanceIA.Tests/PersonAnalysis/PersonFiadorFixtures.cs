// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-29

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using static Api.Web.FinanceIA.Tests.PersonAnalysis.PersonEvaluationFixtures;

namespace Api.Web.FinanceIA.Tests.PersonAnalysis;

/// <summary>Frozen Fiador rows for the reader tests (GET, PATCH .../decision, the PDF): written the way the POST writes them.</summary>
internal static class PersonFiadorFixtures
{
    /// <summary>A Fiador row of <paramref name="analysisId"/> holding one Inmueble (non-housing) of <paramref name="inmueble"/>.</summary>
    public static AnalysisParticipant FiadorRow(Guid analysisId, string tenantId, decimal amountAtCharge, decimal inmueble)
    {
        var evaluation = PersonEvaluationService.FiadorEvaluation(PersonEvaluationFiadorTests.Fiador(
            [PersonEvaluationFiadorTests.Asset(LegajoItemKinds.Inmueble, inmueble)],
            person: Empty() with { RequestedAmount = amountAtCharge }));

        return new AnalysisParticipant
        {
            ParticipantId        = Guid.NewGuid(),
            TenantId             = tenantId,
            AnalysisId           = analysisId,
            PersonLegajoId       = Guid.NewGuid(),
            Role                 = ParticipantRoles.Fiador,
            FiadorId             = Guid.NewGuid(),
            GuaranteeType        = GuaranteeTypes.Fianza,
            AmountAtCharge       = amountAtCharge,
            Currency             = "ARS",
            FiltersJson          = PersonEvaluationJson.Serialize(evaluation.Filters),
            IndicatorsJson       = PersonEvaluationJson.Serialize(evaluation.Indicators),
            CoverageJson         = PersonEvaluationJson.Serialize(evaluation.Coverage),
            DocumentVersionsJson = "{}",
            EvaluationVersion    = PersonEvaluationService.Version,
            CreatedAt            = AsOfUtc
        };
    }

    /// <summary>
    /// The Fiador row the POST writes for <paramref name="input"/>, evaluated by the REAL engine: its amount at
    /// charge and currency are the operation's (<c>input.Person</c>), the guarantee terms are the input's.
    /// </summary>
    public static AnalysisParticipant FiadorRow(Guid analysisId, string tenantId, FiadorEvaluationInput input, Guid? fiadorId = null)
    {
        var evaluation = PersonEvaluationService.FiadorEvaluation(input);

        return new AnalysisParticipant
        {
            ParticipantId         = Guid.NewGuid(),
            TenantId              = tenantId,
            AnalysisId            = analysisId,
            PersonLegajoId        = Guid.NewGuid(),
            Role                  = ParticipantRoles.Fiador,
            FiadorId              = fiadorId ?? Guid.NewGuid(),
            GuaranteeType         = input.GuaranteeType,
            AmountAtCharge        = input.Person.RequestedAmount,
            Currency              = input.Person.Currency,
            InstallmentAmount     = input.Person.InstallmentAmount,
            SpouseConsent         = input.SpouseConsent,
            HousingInopposable    = input.HousingInopposable,
            GuaranteesGivenAmount = input.GuaranteesGivenAmount,
            FiltersJson           = PersonEvaluationJson.Serialize(evaluation.Filters),
            IndicatorsJson        = PersonEvaluationJson.Serialize(evaluation.Indicators),
            CoverageJson          = PersonEvaluationJson.Serialize(evaluation.Coverage),
            DocumentVersionsJson  = PersonEvaluationJson.Serialize(new PersonEvaluationSources
            {
                Documents         = [],
                BcraDataUpdatedAt = input.Person.BcraUpdatedAt,
                ArcaDataUpdatedAt = input.Person.ArcaUpdatedAt,
                Parameters        = input.Person.Parameters
            }),
            EvaluationVersion     = evaluation.Version,
            CreatedAt             = input.Person.AsOfUtc
        };
    }
}
