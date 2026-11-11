// Project : Api.Web.FinanceIA
// Created : 2026-11-09

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>Where the one automatic retry of a background run is, on THIS instance.</summary>
public enum AnalysisAutoRetryState
{
    /// <summary>Waiting for its delay; the document stays in Processing.</summary>
    Scheduled,

    /// <summary>Queued again after the delay, or executing.</summary>
    Running
}

/// <summary>
/// What <see cref="AnalysisRunRegistry"/> remembers about a run's automatic retry, for GET
/// /api/analyses/runs. In memory only: a restart loses it, together with the retry itself.
/// </summary>
/// <param name="TenantId">Tenant that owns the document; a lookup for another tenant gets nothing.</param>
/// <param name="State">Scheduled or running.</param>
/// <param name="ScheduledAtUtc">When the retry was due to start; null only if it was never scheduled here.</param>
public sealed record AnalysisAutoRetryInfo(string TenantId, AnalysisAutoRetryState State, DateTime? ScheduledAtUtc);
