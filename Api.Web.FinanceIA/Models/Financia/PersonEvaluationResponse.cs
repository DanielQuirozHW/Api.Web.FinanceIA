// Project : Api.Web.FinanceIA
// Created : 2026-10-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The persona humana evaluation of one participant, returned on <see cref="AnalysisResultResponse.PersonEvaluation"/>
/// (M10 Phase 4a). Read from the frozen AnalysisParticipants row, never recomputed.
/// </summary>
public sealed record PersonEvaluationResponse
{
    /// <summary>The participant row.</summary>
    public required Guid ParticipantId { get; init; }

    /// <summary>The person's legajo.</summary>
    public required Guid PersonLegajoId { get; init; }

    /// <summary>One of <see cref="ParticipantRoles.Known"/>.</summary>
    public required string Role { get; init; }

    /// <summary>The amount at the person's charge.</summary>
    public required decimal AmountAtCharge { get; init; }

    /// <summary>ISO code of the amounts.</summary>
    public required string Currency { get; init; }

    /// <summary>The cuota of the requested operation, when stated.</summary>
    public decimal? InstallmentAmount { get; init; }

    /// <summary>
    /// How many cuotas a year <see cref="InstallmentAmount"/> is (1, 2, 4 or 12), as the evaluation read it
    /// (engine 0.7). Null on an evaluation frozen before 0.7 — a monthly cuota — and on a fiador.
    /// </summary>
    public int? CuotasPorAnio { get; init; }

    /// <summary>
    /// The monthly cuotas vigentes as declared by the analyst (engine 0.8); null when not declared (they were
    /// estimated from the Central de Deudores) and on every evaluation frozen before 0.8.
    /// </summary>
    public decimal? CuotasVigentesDeclaradas { get; init; }

    /// <summary>
    /// The agro annual mode as resolved (engine 0.7): active or not, its origin (actividad | analista), the
    /// analyst's override, the main CLAE and the campaign's first month. Null on an evaluation frozen before 0.7
    /// and on a fiador, which never runs in agro mode.
    /// </summary>
    public PersonAgroMode? AgroMode { get; init; }

    /// <summary>The engine version that produced the row.</summary>
    public required string EvaluationVersion { get; init; }

    /// <summary>One of <see cref="PersonSuggestions"/>, derived from <see cref="Filters"/>.</summary>
    public required string Suggestion { get; init; }

    /// <summary>The pre-filters.</summary>
    public required IReadOnlyList<PersonFilterResult> Filters { get; init; }

    /// <summary>The indicators.</summary>
    public required IReadOnlyList<PersonIndicatorResult> Indicators { get; init; }

    /// <summary>
    /// What approving this analysis requires, from the tenant's current rules; null outside the detail,
    /// POST and decision responses.
    /// </summary>
    public PersonDecisionRules? DecisionRules { get; init; }

    /// <summary>The documentary coverage; null only if the stored JSON could not be read.</summary>
    public PersonCoverageResult? Coverage { get; init; }

    /// <summary>What the evaluation read; null only if the stored JSON could not be read.</summary>
    public PersonEvaluationSources? Sources { get; init; }

    /// <summary>When the evaluation was made.</summary>
    public required DateTime CreatedAt { get; init; }

    /// <summary>
    /// The fiadores of the analysis (M10 Phase 5a-2), each on his own frozen row; null when the analysis has
    /// none, and outside the detail, POST and decision responses.
    /// </summary>
    public IReadOnlyList<PersonFiadorEvaluationResponse>? Fiadores { get; init; }

    /// <summary>
    /// The aggregate counter-guarantee coverage of <see cref="Fiadores"/>, the same value as
    /// <see cref="PersonDecisionRules.CounterGuaranteeCoverage"/>; null when the analysis has no fiadores.
    /// </summary>
    public CounterGuaranteeCoverage? CounterGuaranteeCoverage { get; init; }
}
