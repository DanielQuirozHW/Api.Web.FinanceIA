// Project : Api.Web.FinanceIA
// Created : 2026-11-03

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One <c>Analysis.Failed</c> audit row of a document, narrowed to what GET /api/analyses/runs reads:
/// when it was written, by whom (the requester; null for the stale-Processing sweep) and its
/// NewValueJson (<c>{"reason": "..."}</c>, or null). A REPOSITORY projection, not a wire contract.
/// </summary>
public sealed class AnalysisRunFailureAuditProjection
{
    public Guid DocumentId { get; init; }

    /// <summary>
    /// <c>Analysis.Failed</c> or <c>Analysis.AutoRetryFinished</c> (<c>{"outcome": …, "reason": …}</c>).
    /// Defaults to <c>Analysis.Failed</c>.
    /// </summary>
    public string Action { get; init; } = "Analysis.Failed";

    public DateTime Timestamp { get; init; }

    public Guid? UserId { get; init; }

    public string? NewValueJson { get; init; }
}
