// Project : Api.Web.FinanceIA
// Created : 2026-10-30

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.PersonEvaluation;

/// <summary>The evaluation of one fiador on his own legajo, with the four evaluation columns of his row.</summary>
public sealed record PersonFiadorEvaluation
{
    public required FiadorEvaluationResult  Evaluation           { get; init; }
    public required PersonEvaluationSources Sources              { get; init; }

    /// <summary>The legajo the fiador was evaluated on.</summary>
    public required Guid                    PersonLegajoId       { get; init; }

    public required string                  DocumentVersionsJson { get; init; }
    public required string                  FiltersJson          { get; init; }
    public required string                  IndicatorsJson       { get; init; }
    public required string                  CoverageJson         { get; init; }
}
