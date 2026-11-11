// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-30

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;

namespace Api.Web.FinanceIA.Tests.PersonAnalysis;

/// <summary>
/// What an evaluation frozen by engine 0.5 looks like: its parameters carry the retired plazo and none of the 0.6
/// members, and its cuota indicators carry plazoEstimadoDeudaMeses instead of cuotaEstimadaPctSaldo.
/// </summary>
internal static class PersonEvaluationPre06Fixtures
{
    /// <summary>The 0.6 members a 0.5 blob does not have.</summary>
    public static readonly string[] V06ParameterMembers =
    [
        "cuotaEstimadaPctSaldo", "consumoMesBase", "haircutDeposito", "haircutInversion", "haircutCredito", "haircutOtro"
    ];

    /// <summary>The 0.7 members (agro annual mode) no blob frozen before 0.7 has.</summary>
    public static readonly string[] V07ParameterMembers = ["agroModoAnual", "agroCuotasPorAnio", "agroMesInicioCampania"];

    /// <summary>A 0.5 PersonEvaluationParameters blob: plazo 24, verde 1,3, no 0.6 or 0.7 member.</summary>
    public static JsonObject Parameters()
    {
        var json = JsonNode.Parse(PersonEvaluationJson.Serialize(PersonEvaluationParameters.Defaults))!.AsObject();
        foreach (var member in V06ParameterMembers.Concat(V07ParameterMembers))
            json.Remove(member);

        json["plazoEstimadoDeudaMeses"] = 24;
        json["dscrVerde"]               = 1.3m;
        return json;
    }

    /// <summary>A 0.5 AnalysisParticipants.DocumentVersionsJson.</summary>
    public static string SourcesJson()
    {
        var sources = JsonNode.Parse(PersonEvaluationJson.Serialize(new PersonEvaluationSources
        {
            Documents  = [],
            Parameters = PersonEvaluationParameters.Defaults
        }))!.AsObject();

        sources["parameters"] = Parameters();
        return sources.ToJsonString();
    }

    /// <summary>A 0.5 cuota_ingreso: the balance spread over 24 months.</summary>
    public static PersonIndicatorResult CuotaIngreso() => new()
    {
        Id           = "cuota_ingreso",
        Status       = PersonIndicatorStatuses.Ok,
        Value        = 0.3m,
        Unit         = "ratio",
        Band         = PersonIndicatorBands.Amarillo,
        EvidenceKind = PersonEvidenceKinds.Verificado,
        Evidence     = "Cuota $ 200.000 más cuotas vigentes estimadas $ 100.000 (saldo de la Central de Deudores en 24 meses) " +
                       "sobre ingreso mensual $ 1.000.000 según Ganancias 2025 (rentas de 3ª y 4ª categoría): 30 %.",
        Inputs       = new Dictionary<string, string?>
        {
            ["corteBajo"]                  = "0.25",
            ["corteMedio"]                 = "0.35",
            ["corteAlto"]                  = "0.5",
            ["cuota"]                      = "200000",
            ["cuotasVigentesEstimadas"]    = "100000",
            ["plazoEstimadoDeudaMeses"]    = "24",
            ["cuotasVigentesEsEstimacion"] = "true",
            ["ingresoMensual"]             = "1000000"
        }
    };
}
