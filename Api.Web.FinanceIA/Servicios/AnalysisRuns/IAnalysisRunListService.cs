// Project : Api.Web.FinanceIA
// Created : 2026-11-03

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>
/// Builds GET /api/analyses/runs: what is running, what failed and what just finished, for one tenant.
/// The tenant and the caller's permission arrive as parameters — this service never reads the tenant
/// accessor or a claim.
/// </summary>
public interface IAnalysisRunListService
{
    /// <param name="tenantId">The resolved tenant; every read is scoped to it.</param>
    /// <param name="days">Failed-list window in days, already validated to 1..30 by the controller.</param>
    /// <param name="callerCanTrigger">True when the caller holds a role that may POST /api/analyses.</param>
    /// <param name="nowUtc">The instant both windows are measured back from.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<AnalysisRunsResponse> GetRunsAsync(
        string tenantId, int days, bool callerCanTrigger, DateTime nowUtc, CancellationToken ct);
}
