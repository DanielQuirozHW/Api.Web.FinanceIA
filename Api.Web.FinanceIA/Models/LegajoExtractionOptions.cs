// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// The Anthropic settings the legajo EXTRACTION reads (M10 Phase 2b), bound from the "Anthropic"
/// configuration section (the one ClaudeService and the classification also read).
/// </summary>
public sealed record LegajoExtractionOptions
{
    /// <summary>
    /// The model id used for extraction by default: <c>claude-sonnet-4-6</c>, the balance extraction's
    /// default too (ExtractionModelCatalog.Default — a test pins the two together). The model must accept
    /// a forced <c>tool_choice</c> and <c>temperature 0</c>: an id outside
    /// <see cref="LegajoModelSupport.ForcedToolUseModels"/> falls back to this default with an Error.
    /// </summary>
    public const string DefaultModel = "claude-sonnet-4-6";

    /// <summary><c>Anthropic:ApiKey</c> — a secret; user-secrets or App Service settings only.</summary>
    public string? ApiKey { get; init; }

    /// <summary><c>Anthropic:LegajoExtractionModel</c>, guarded by <see cref="LegajoModelSupport"/>.</summary>
    public string LegajoExtractionModel { get; init; } = DefaultModel;
}
