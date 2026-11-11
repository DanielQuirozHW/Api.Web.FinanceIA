// Project : Api.Web.FinanceIA
// Created : 2026-10-23

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>One requester's share of the tenant's PendingReview queue, grouped in SQL.</summary>
/// <param name="UserId">Analyses.RequestedBy.</param>
/// <param name="Pending">Their rows with ReviewedBy null.</param>
/// <param name="Returned">Their rows with ReviewedBy set.</param>
/// <param name="OldestGeneratedAt">The oldest AnalysisResults.GeneratedAt among them.</param>
public sealed record DashboardRequesterQueueRow(Guid UserId, int Pending, int Returned, DateTime OldestGeneratedAt);
