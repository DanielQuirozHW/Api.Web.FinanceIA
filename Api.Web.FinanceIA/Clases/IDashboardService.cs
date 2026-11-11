// Project : Api.Web.FinanceIA
// Created : 2026-10-23

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Builds the role dashboards — GET /api/dashboard/my-work, approvals, portfolio, oversight and operations.
/// </summary>
/// <remarks>
/// Read-only. Every section is computed independently: one that throws is logged (section name and
/// ids only), returned as null and named in <c>degradedSections</c>, and the rest of the response
/// still goes out. The tenant and the caller are passed in explicitly — this service never reads the
/// tenant accessor — and so is the clock, so "now" is one instant for the whole response.
/// </remarks>
public interface IDashboardService
{
    /// <summary>The caller's own work: pending, returned, BCRA-degraded, awaiting signature, recent.</summary>
    /// <param name="tenantId">The resolved tenant slug.</param>
    /// <param name="currentUser">The caller's Users row, already verified to belong to <paramref name="tenantId"/>.</param>
    /// <param name="nowUtc">The instant the response describes.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<MyWorkResponse> GetMyWorkAsync(string tenantId, User currentUser, DateTime nowUtc, CancellationToken ct);

    /// <summary>What waits for the caller's signature, and what they decided lately.</summary>
    /// <param name="tenantId">The resolved tenant slug.</param>
    /// <param name="currentUser">
    /// The caller's Users row, already verified to belong to <paramref name="tenantId"/> and to hold a
    /// confirmer role (<see cref="Roles.IsConfirmer"/>) on that row.
    /// </param>
    /// <param name="nowUtc">The instant the response describes.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<ApprovalsResponse> GetApprovalsAsync(string tenantId, User currentUser, DateTime nowUtc, CancellationToken ct);

    /// <summary>
    /// The tenant's live portfolio: totals, risk, top exposures, sectors, maturities, BCRA signals and
    /// the cupos at 90% utilization or more.
    /// Every amount is per currency.
    /// </summary>
    /// <param name="tenantId">The resolved tenant slug.</param>
    /// <param name="nowUtc">The instant the response describes.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<PortfolioResponse> GetPortfolioAsync(string tenantId, DateTime nowUtc, CancellationToken ct);

    /// <summary>
    /// The tenant's review work: the queue, approvals against the suggestion, recent decisions and
    /// re-scores, and monthly throughput.
    /// </summary>
    /// <param name="tenantId">The resolved tenant slug.</param>
    /// <param name="nowUtc">The instant the response describes.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<OversightResponse> GetOversightAsync(string tenantId, DateTime nowUtc, CancellationToken ct);

    /// <summary>Integration health from ApiCallLogs, and source snapshot freshness.</summary>
    /// <param name="tenantId">The resolved tenant slug.</param>
    /// <param name="nowUtc">The instant the response describes.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<OperationsResponse> GetOperationsAsync(string tenantId, DateTime nowUtc, CancellationToken ct);
}
