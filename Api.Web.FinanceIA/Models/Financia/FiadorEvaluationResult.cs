// Project : Api.Web.FinanceIA
// Created : 2026-10-29

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>The evaluation of one fiador (M10 Phase 5a-2), before it is frozen on its AnalysisParticipants row.</summary>
public sealed record FiadorEvaluationResult
{
    /// <summary>The engine version.</summary>
    public required string Version { get; init; }

    /// <summary>One of <see cref="PersonSuggestions"/>, from the fiador's own pre-filters.</summary>
    public required string Suggestion { get; init; }

    /// <summary>The pre-filters reused from the solicitante (BCRA, cheques, ARCA).</summary>
    public required IReadOnlyList<PersonFilterResult> Filters { get; init; }

    /// <summary>cobertura_patrimonial_fiador and cuota_ingreso_estres.</summary>
    public required IReadOnlyList<PersonIndicatorResult> Indicators { get; init; }

    /// <summary>The executable patrimony and its asset breakdown.</summary>
    public required FiadorCoverageResult Coverage { get; init; }
}
