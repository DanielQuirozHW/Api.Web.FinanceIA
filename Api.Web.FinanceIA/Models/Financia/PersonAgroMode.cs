// Project : Api.Web.FinanceIA
// Created : 2026-10-30

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// How a persona humana evaluation (engine 0.7) read the time base of the operation: whether the solicitante is
/// evaluated in ANNUAL mode because his main activity is agro, and how many cuotas a year the stated cuota is.
/// Resolved once per evaluation and frozen in AnalysisParticipants.DocumentVersionsJson
/// (<see cref="PersonEvaluationSources.Agro"/>); absent on every evaluation frozen before 0.7.
/// </summary>
/// <remarks>
/// Only the solicitante carries one. A fiador keeps his own evaluation, which never reads it.
/// </remarks>
public sealed record PersonAgroMode
{
    /// <summary>True when the evaluation runs in agro annual mode (campaign window, seasonal recency).</summary>
    public required bool Active { get; init; }

    /// <summary>Who decided <see cref="Active"/>: <see cref="PersonAgroModeOrigins"/>.</summary>
    public required string Origin { get; init; }

    /// <summary>The analyst's override as stated (true | false); null when the mode was detected.</summary>
    public bool? Override { get; init; }

    /// <summary>The main ARCA activity code (CLAE, six digits); null when the ARCA snapshot has none.</summary>
    public string? Clae { get; init; }

    /// <summary>How many cuotas a year the stated cuota is (1, 2, 4 or 12).</summary>
    public required int CuotasPorAnio { get; init; }

    /// <summary>The first month of the campaign (1-12), pf_agro_mes_inicio_campania.</summary>
    public required int MesInicioCampania { get; init; }

    /// <summary>The evidence sentence that says which rule applied; null when nothing needs saying.</summary>
    public string? Evidence { get; init; }

    /// <summary>
    /// True when the debt service is compared on an ANNUAL base: in agro mode, or whenever the cuota is not
    /// monthly. False only for a monthly cuota outside agro — the 0.6 formulas, unchanged.
    /// </summary>
    [JsonIgnore]
    public bool Annualized => Active || CuotasPorAnio != 12;
}
