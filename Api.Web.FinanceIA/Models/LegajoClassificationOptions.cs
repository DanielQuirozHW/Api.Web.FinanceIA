// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// The Anthropic settings the legajo classification reads, bound from the "Anthropic" configuration
/// section (the one ClaudeService also reads).
/// </summary>
public sealed record LegajoClassificationOptions
{
    /// <summary>The model id used for classification. Default <c>claude-haiku-4-5</c>, the id NarrativeModel already uses.</summary>
    public const string DefaultModel = "claude-haiku-4-5";

    /// <summary><c>Anthropic:ApiKey</c> — a secret; user-secrets or App Service settings only.</summary>
    public string? ApiKey { get; init; }

    /// <summary><c>Anthropic:LegajoClassificationModel</c>.</summary>
    public string LegajoClassificationModel { get; init; } = DefaultModel;
}
