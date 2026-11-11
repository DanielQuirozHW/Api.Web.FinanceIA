// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// USD per million tokens for the Claude models the legajo pipeline and the balance extraction may
/// be configured to call (<c>Anthropic:LegajoClassificationModel</c>, <c>Anthropic:ExtractionModel</c>).
/// Used to fill LegajoExtractions.CostUsd and ApiCallLogs.EstimatedCostUsd. Thinking tokens are billed
/// as output and are already inside the response's output_tokens, so input × input price + output ×
/// output price stays the whole estimate for a thinking model too.
/// </summary>
/// <remarks>
/// Deliberately small and separate from <see cref="ChatModelCatalog"/>: that catalogue is the CHAT
/// allow-list, and retiring a chat model there must not silently null the cost of a classification.
/// Tests assert the two agree on every id they share, and that every
/// <see cref="ExtractionModelCatalog"/> entry is priced here. An id missing here costs <c>null</c> —
/// "not known", never zero.
/// </remarks>
public static class ClaudeModelPricing
{
    /// <summary>(input, output) USD per million tokens, keyed by exact model id.</summary>
    public static readonly IReadOnlyDictionary<string, (decimal Input, decimal Output)> PerMillionTokens =
        new Dictionary<string, (decimal Input, decimal Output)>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-haiku-4-5"]  = (1m, 5m),
            ["claude-sonnet-4-6"] = (3m, 15m),
            ["claude-sonnet-5"]   = (2m, 10m),
            ["claude-sonnet-5-5"] = (2m, 10m),
        };

    /// <summary>Estimated USD cost of one call, or null when <paramref name="model"/> has no price entry.</summary>
    public static decimal? EstimateCostUsd(string? model, int inputTokens, int outputTokens)
    {
        if (string.IsNullOrWhiteSpace(model) || !PerMillionTokens.TryGetValue(model.Trim(), out var price))
            return null;

        return Math.Round((inputTokens * price.Input + outputTokens * price.Output) / 1_000_000m, 6);
    }
}
