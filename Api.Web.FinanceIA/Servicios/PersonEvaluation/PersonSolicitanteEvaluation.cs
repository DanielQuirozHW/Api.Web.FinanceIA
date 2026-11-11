// Project : Api.Web.FinanceIA
// Created : 2026-10-30

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.PersonEvaluation;

/// <summary>
/// The evaluation of the solicitante, with every column it is frozen into: the four evaluation columns of
/// his AnalysisParticipants row and the AnalysisResults columns the persona analysis derives from it.
/// </summary>
public sealed record PersonSolicitanteEvaluation
{
    public required PersonEvaluationResult  Evaluation  { get; init; }
    public required PersonEvaluationSources Sources     { get; init; }

    /// <summary>The solicitante's reconciled inventory: the fiadores' evaluations compare against it.</summary>
    public required LegajoInventoryResponse Inventory   { get; init; }

    /// <summary>The CPI series the evaluation read, shared with the fiadores; null when none was available.</summary>
    public BcraMacroData?                   Macro       { get; init; }

    /// <summary>Where <see cref="Macro"/> came from: <c>legajo</c>, <c>bcra</c> or null.</summary>
    public string?                          MacroSource { get; init; }

    // ── AnalysisParticipants ────────────────────────────────────────────────────
    public required string DocumentVersionsJson { get; init; }
    public required string FiltersJson          { get; init; }
    public required string IndicatorsJson       { get; init; }
    public required string CoverageJson         { get; init; }

    // ── AnalysisResults ─────────────────────────────────────────────────────────
    public string?         BcraDataJson     { get; init; }
    public string?         MacroDataJson    { get; init; }
    public string?         FiscalDataJson   { get; init; }
    public string?         LufeDataJson     { get; init; }
    public required string AiInputHash      { get; init; }
    public required string AiOutputHash     { get; init; }
    public required string EnrichmentStatus { get; init; }
    public string?         EnrichmentNotes  { get; init; }
}
