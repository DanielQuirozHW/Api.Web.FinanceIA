// Project : Api.Web.FinanceIA
// Created : 2026-11-06

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One side of GET /api/analyses/{id}/previous-comparison: the Analyses columns and the AnalysisResults
/// columns the comparison reads, projected in one query (plus, for a persona humana analysis, the
/// <see cref="Person"/> facts read in a second one). <c>BcraDataJson</c> is NOT selected — its six
/// scalars arrive already extracted by <c>JSON_VALUE</c>, exactly as on the analysis detail.
/// </summary>
public sealed class AnalysisComparisonSnapshot
{
    public Guid ResultId { get; set; }

    public Guid AnalysisId { get; set; }

    public Guid EntityId { get; set; }

    public string AnalysisKind { get; set; } = AnalysisKinds.Balance;

    public string Status { get; set; } = string.Empty;

    public int FiscalYear { get; set; }

    public string PromptVersion { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime? ReviewedAt { get; set; }

    public DateTime? PreApprovedAt { get; set; }

    /// <summary>Internal 0–100 scale (0 = healthy); null on an unscored result.</summary>
    public decimal? AiSuggestedScore { get; set; }

    public string? AiSuggestedLevel { get; set; }

    public string? BalanceJson { get; set; }

    public string? RatiosJson { get; set; }

    public string? ScoreBreakdownJson { get; set; }

    /// <summary>Raw <c>$.deuda.estado</c> (a <see cref="BcraDeudaConsultaEstado"/> ordinal as text).</summary>
    public string? BcraDeudaEstadoRaw { get; set; }

    /// <summary>Raw <c>$.deuda.situacionMaxima</c>.</summary>
    public string? BcraDeudaSituacionMaximaRaw { get; set; }

    /// <summary>Raw <c>$.deuda.montoTotal</c>, in MILES de pesos as BCRA reports it.</summary>
    public string? BcraDeudaMontoTotalRaw { get; set; }

    /// <summary>Raw <c>$.cheques.estado</c> (a <see cref="BcraChequesConsultaEstado"/> ordinal as text).</summary>
    public string? BcraChequesEstadoRaw { get; set; }

    /// <summary>Raw <c>$.cheques.totalRechazados</c>.</summary>
    public string? BcraChequesTotalRechazadosRaw { get; set; }

    /// <summary>Raw <c>$.cheques.montoTotalRechazado</c>, in pesos.</summary>
    public string? BcraChequesMontoTotalRechazadoRaw { get; set; }

    /// <summary>
    /// Persona humana only: the solicitante's frozen evaluation and the terms, loaded by
    /// <c>IAnalysisComparisonRepository.GetPersonFactsAsync</c>; null for a company analysis and for a persona
    /// analysis with no stored evaluation.
    /// </summary>
    public PersonComparisonFacts? Person { get; set; }

    /// <summary>When the analysis was decided: the final decision, else the pre-approval; null while pending.</summary>
    public DateTime? DecidedAt => ReviewedAt ?? PreApprovedAt;
}
