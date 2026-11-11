// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>A current extraction run evaluated on its effective values.</summary>
/// <param name="Schema">The run's schema.</param>
/// <param name="Run">The run.</param>
/// <param name="Corrections">Its corrections, oldest first.</param>
/// <param name="Effective">ResultJson with the corrections applied.</param>
/// <param name="CorrectedPaths">The paths the corrections set.</param>
/// <param name="Assessment">Checks and queues on the effective values.</param>
public sealed record LegajoEffectiveExtraction(
    LegajoExtractionSchema Schema,
    LegajoExtraction Run,
    IReadOnlyList<LegajoCorrection> Corrections,
    JsonObject Effective,
    IReadOnlyList<string> CorrectedPaths,
    LegajoExtractionAssessment Assessment);
