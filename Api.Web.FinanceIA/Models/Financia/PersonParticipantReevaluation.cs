// Project : Api.Web.FinanceIA
// Created : 2026-10-30

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The new evaluation of one AnalysisParticipants row, ready to overwrite it in place: the row is located
/// by <see cref="ParticipantId"/> and only these columns change — never its identity (FiadorId,
/// PersonLegajoId, Role) or the terms it was evaluated on.
/// </summary>
public sealed record PersonParticipantReevaluation
{
    public required Guid     ParticipantId        { get; init; }
    public required string   DocumentVersionsJson { get; init; }
    public required string   FiltersJson          { get; init; }
    public required string   IndicatorsJson       { get; init; }
    public required string   CoverageJson         { get; init; }
    public required string   EvaluationVersion    { get; init; }

    /// <summary>The instant of the evaluation (its AsOf), as the POST stores it at creation.</summary>
    public required DateTime CreatedAt            { get; init; }
}
