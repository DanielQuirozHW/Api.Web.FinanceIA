// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using System.Text.Json;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Body of POST /api/analyses/{resultId}/balance-corrections: the figures of the extracted balance an
/// analyst corrects by hand, and why. Validated by <c>BalanceCorrectionValidator</c> — nothing here is
/// trusted as sent.
/// </summary>
public sealed class BalanceCorrectionRequest
{
    /// <summary>Why the figures are corrected. Required, 10..1000 characters once trimmed.</summary>
    public string? Reason { get; set; }

    /// <summary>1..30 corrections, each to a different path.</summary>
    public List<BalanceCorrectionItemRequest>? Corrections { get; set; }

    /// <summary>
    /// Where the figures come from: <c>"manual"</c> (the default when absent or null) or
    /// <c>"ai_suggestion"</c> (accepted from POST …/balance-corrections/suggestions). Closed vocabulary
    /// (<see cref="BalanceCorrectionOrigins"/>): anything else is a 400.
    /// </summary>
    public string? Origin { get; set; }
}

/// <summary>One corrected figure.</summary>
public sealed class BalanceCorrectionItemRequest
{
    /// <summary>
    /// A path from the closed whitelist: one of the 13 top-level figures (e.g. <c>current_assets</c>) or
    /// <c>current_assets_items[i].amount</c>, <c>current_liabilities_items[i].amount</c>,
    /// <c>non_current_liabilities_items[i].amount</c> with an existing index <c>i</c>.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// A finite JSON number in the SAME unit as stored — pesos, after amounts_scale — or JSON null to clear
    /// the figure. Kept raw so a string, a bool or a missing value is told apart from a number.
    /// </summary>
    public JsonElement Value { get; set; }
}
