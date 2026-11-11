// Project : Api.Web.FinanceIA
// Created : 2026-10-22

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// The re-score switches, bound from the "Rescore" configuration section. Every value has a working
/// default, so an environment with no section runs the documented behaviour.
/// </summary>
public sealed record RescoreOptions
{
    /// <summary>
    /// The minimum time, in seconds, between two re-scores of the SAME analysis. Default 120. Read off
    /// AnalysisResults.RescoredAt rather than an in-process cache, so it holds across App Service
    /// instances and restarts. Values below 0 read as 0 (no cooldown).
    /// </summary>
    public int CooldownSeconds { get; init; } = 120;

    /// <summary>
    /// The Retry-After, in seconds, of a 503 <c>bcra_unavailable</c>. Default 60.
    /// </summary>
    public int BcraRetryAfterSeconds { get; init; } = 60;
}
