// Project : Api.Web.FinanceIA
// Created : 2026-10-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// What approving a persona humana analysis requires (M10 Phase 4a), resolved from the tenant's current
/// <c>pf_</c> rules and the frozen suggestion, so the Portal never hardcodes them. PATCH .../decision
/// enforces exactly these.
/// </summary>
public sealed record PersonDecisionRules
{
    /// <summary>The two confirmation flags every approval of a persona analysis must carry as true.</summary>
    public static readonly IReadOnlyList<string> Confirmations = ["counterGuaranteesConfirmed", "fdrLimitConfirmed"];

    /// <summary>
    /// An approval without reviewNotes is 400 reason_required: pf_aprobacion_requiere_motivo is on, or the
    /// analysis has fiadores whose aggregate coverage is below the minimum (<see cref="CounterGuaranteeBelowMin"/>).
    /// </summary>
    public required bool RequiresReason { get; init; }

    /// <summary>
    /// The suggestion is RechazoSugerido and pf_rechazo_sugerido_requiere_preaprobacion is on: approving goes
    /// through pre-approval and quorum whatever the amount or the role, and the pre-approver cannot confirm.
    /// </summary>
    public required bool RequiresPreApproval { get; init; }

    /// <summary>The request fields that must be true on every approval (400 confirmations_required otherwise).</summary>
    public IReadOnlyList<string> RequiredConfirmations { get; init; } = Confirmations;

    /// <summary>
    /// pf_contragarantia_cobertura_min_pct: the share of the aval (whole percent, 0-100) the counter-guarantee
    /// must cover. Above 0 no operation of the approval may be a sola firma.
    /// </summary>
    public required int MinCounterGuaranteeCoveragePct { get; init; }

    /// <summary>
    /// An operation of the approval may carry aSolaFirma = true. False whenever a counter-guarantee coverage is
    /// required (400 person_sola_firma_not_allowed otherwise).
    /// </summary>
    public bool SolaFirmaAllowed => MinCounterGuaranteeCoveragePct <= 0;

    /// <summary>
    /// The aggregate counter-guarantee coverage of the analysis's fiadores (M10 Phase 5a-2); null when it has
    /// none. Informative for the decision: the counterGuaranteesConfirmed checkbox stays the client's call.
    /// </summary>
    public CounterGuaranteeCoverage? CounterGuaranteeCoverage { get; init; }

    /// <summary>
    /// The analysis has fiadores and their aggregate coverage does not meet the minimum. Approving still works,
    /// but only with a reason, and the Portal warns.
    /// </summary>
    public bool CounterGuaranteeBelowMin { get; init; }

    /// <summary>Resolves the rules.</summary>
    public static PersonDecisionRules From(PersonEvaluationParameters parameters, string suggestion)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        return new PersonDecisionRules
        {
            RequiresReason      = parameters.AprobacionRequiereMotivo,
            RequiresPreApproval = parameters.RechazoSugeridoRequierePreaprobacion
                                  && suggestion == PersonSuggestions.RechazoSugerido,
            MinCounterGuaranteeCoveragePct = parameters.ContragarantiaCoberturaMinPct
                                             ?? (int)PersonEvaluationDefaults.Get("pf_contragarantia_cobertura_min_pct")
        };
    }

    /// <summary>
    /// These rules with the aggregate coverage of the analysis's fiadores applied: below the minimum, a reason
    /// becomes mandatory. A null <paramref name="coverage"/> (no fiadores) changes nothing.
    /// </summary>
    public PersonDecisionRules WithCounterGuaranteeCoverage(CounterGuaranteeCoverage? coverage)
    {
        var belowMin = coverage is { MeetsMin: false };
        return this with
        {
            CounterGuaranteeCoverage = coverage,
            CounterGuaranteeBelowMin = belowMin,
            RequiresReason           = RequiresReason || belowMin
        };
    }
}
