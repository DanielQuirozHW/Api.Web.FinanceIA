// Project : Api.Web.FinanceIA
// Created : 2026-06-07

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Result returned by <see cref="Api.Web.FinanceIA.Servicios.IClaudeService.GenerateScoringNarrativeAsync"/>.
/// </summary>
public sealed record ClaudeNarrativeResult(
    /// <summary>The narrative text in Spanish, or empty on failure.</summary>
    string Text,
    /// <summary>Number of input tokens consumed.</summary>
    int InputTokens,
    /// <summary>Number of output tokens generated.</summary>
    int OutputTokens
);
