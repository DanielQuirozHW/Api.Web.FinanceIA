// Project : Api.Web.FinanceIA
// Created : 2026-10-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>The output of one persona humana evaluation (M10 Phase 4a): flags and figures, no score.</summary>
public sealed record PersonEvaluationResult
{
    /// <summary>The engine version.</summary>
    public required string Version { get; init; }

    /// <summary>One of <see cref="PersonSuggestions"/>.</summary>
    public required string Suggestion { get; init; }

    /// <summary>The pre-filters.</summary>
    public required IReadOnlyList<PersonFilterResult> Filters { get; init; }

    /// <summary>The indicators.</summary>
    public required IReadOnlyList<PersonIndicatorResult> Indicators { get; init; }

    /// <summary>The documentary coverage.</summary>
    public required PersonCoverageResult Coverage { get; init; }
}
