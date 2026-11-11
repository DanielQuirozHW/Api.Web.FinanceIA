// Project : Api.Web.FinanceIA
// Created : 2026-11-01

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// The balance-extraction model settings, bound from the "Anthropic" configuration section (the one
/// ClaudeService and the legajo services already read). All three are optional: with none set, the
/// extraction sends the default request (claude-sonnet-4-6, temperature 0, max_tokens 16000, 120 s per
/// attempt). Values are validated by <c>ExtractionModelSelection</c>, never trusted.
/// </summary>
public sealed record ExtractionModelOptions
{
    /// <summary>
    /// <c>Anthropic:ExtractionModel</c> — an id from <c>ExtractionModelCatalog</c>. Unset or blank
    /// means the default; an unknown id logs an Error once and falls back to the default.
    /// </summary>
    public string? ExtractionModel { get; init; }

    /// <summary>
    /// <c>Anthropic:ExtractionEffort</c> — low | medium | high | xhigh | max, sent as
    /// <c>output_config.effort</c> only for a model that supports it. Unset means the API default
    /// (high); an invalid value logs an Error once and is not sent.
    /// </summary>
    public string? ExtractionEffort { get; init; }

    /// <summary>
    /// <c>Anthropic:ExtractionTimeoutSeconds</c> — per-attempt timeout overriding the model's own
    /// (120 s for Sonnet 4.6, 300 s for Sonnet 5.x), clamped to 30..900.
    /// </summary>
    public int? ExtractionTimeoutSeconds { get; init; }
}
