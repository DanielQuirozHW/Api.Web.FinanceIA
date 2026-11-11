// Project : Api.Web.FinanceIA
// Created : 2026-10-22

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The <c>rescore</c> block of the POST /api/analyses/{id}/rescore response: the score before and after,
/// and the sources the new one was computed from.
/// </summary>
/// <remarks>
/// A persona humana re-evaluation has no score and no level, so those four stay null; what changed is
/// told instead by the members below the positional ones. Each is OMITTED when null, so a balance
/// re-score answers exactly the shape it always did.
/// </remarks>
public sealed record AnalysisRescoreSummary(
    decimal?                      PreviousScore,
    decimal?                      NewScore,
    string?                       PreviousLevel,
    string?                       NewLevel,
    string                        PreviousEnrichmentStatus,
    string                        NewEnrichmentStatus,
    DateTime                      RescoredAt,
    IReadOnlyList<RescoreSource>  Sources)
{
    /// <summary>Persona humana: the solicitante's suggestion before the re-evaluation (<see cref="PersonSuggestions"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreviousSuggestion { get; init; }

    /// <summary>Persona humana: the solicitante's suggestion after it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NewSuggestion { get; init; }

    /// <summary>Persona humana: the solicitante's pre-filter counts by outcome, before and after.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PersonRescoreFilterCounts? FilterCounts { get; init; }

    /// <summary>
    /// Persona humana: cobertura_servicio_deuda, cuota_ingreso and solvencia_pn of the solicitante, before
    /// and after.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PersonRescoreIndicatorChange>? Indicators { get; init; }

    /// <summary>Persona humana with fiadores: their aggregate counter-guarantee coverage, before and after.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PersonRescoreCoverageChange? CounterGuaranteeCoverage { get; init; }

    /// <summary>Persona humana: the solicitante row's EvaluationVersion before the re-evaluation.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreviousEvaluationVersion { get; init; }

    /// <summary>Persona humana: the engine version that produced the new evaluation.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NewEvaluationVersion { get; init; }

    /// <summary>Persona humana: how many fiadores were re-evaluated with the solicitante.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FiadoresCount { get; init; }
}
