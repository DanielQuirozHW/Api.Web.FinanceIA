// Project : Api.Web.FinanceIA
// Created : 2026-11-03

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Where a balance correction came from — the closed vocabulary of
/// <see cref="BalanceCorrectionRequest.Origin"/>, stored on each <c>manual_corrections</c> entry and
/// exposed by GET /api/analyses/{id}/balance-corrections. Part of the Portal contract.
/// </summary>
public static class BalanceCorrectionOrigins
{
    /// <summary>The analyst typed the figure (the default).</summary>
    public const string Manual = "manual";

    /// <summary>The analyst accepted a figure proposed by POST …/balance-corrections/suggestions.</summary>
    public const string AiSuggestion = "ai_suggestion";

    /// <summary>Every accepted value.</summary>
    public static readonly IReadOnlyList<string> All = [Manual, AiSuggestion];
}
