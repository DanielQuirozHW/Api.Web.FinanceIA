// Project : Api.Web.FinanceIA
// Created : 2026-08-31

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// API shape of a persisted <see cref="TenantBillingConfig"/> row, returned by
/// PUT /api/billing/config.
///
/// <para>
/// Echoes the row that was just written rather than a usage snapshot: the endpoint is an
/// idempotent upsert, so what the caller wants back is the state it just established. The Portal
/// refreshes its quota banner from GET /api/billing/usage, which recounts consumption.
/// </para>
/// </summary>
public sealed record BillingConfigResponse
{
    /// <summary>Display label stored on the row, e.g. "200 análisis".</summary>
    public required string PlanName { get; init; }

    /// <summary>Analyses the tenant may run per calendar month.</summary>
    public required int AnalysisMonthlyLimit { get; init; }

    /// <summary>Claude chat tokens the tenant may spend per calendar month.</summary>
    public required long ChatTokensMonthlyLimit { get; init; }

    /// <summary>
    /// Claude tokens a single chat conversation's context may occupy. Not a share of the monthly
    /// pool above — a separate ceiling on one prompt. Not enforced yet.
    /// </summary>
    public required int ChatContextBudgetTokens { get; init; }

    /// <summary>
    /// Ids of the Claude chat models the tenant may use, as stored — normalised to the catalogue's
    /// order and never empty. Echoed as an ARRAY regardless of whether the request sent an array or
    /// a comma-separated string.
    /// </summary>
    public required IReadOnlyList<string> ChatEnabledModels { get; init; }

    /// <summary>Whether the configuration is in force. False resolves the tenant to the minimum plan.</summary>
    public required bool IsActive { get; init; }

    /// <summary>UTC timestamp of this update.</summary>
    public required DateTime UpdatedAt { get; init; }

    /// <summary>Id of the SuperAdmin who made the change.</summary>
    public required string? UpdatedBy { get; init; }
}
