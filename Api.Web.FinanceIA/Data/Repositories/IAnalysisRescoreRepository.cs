// Project : Api.Web.FinanceIA
// Created : 2026-10-22

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Data access for the re-score of a balance analysis (DDL 073): the vote check, the history read and
/// the one atomic write. Every method is scoped by tenant — FinanciaDbContext has no global filter.
/// </summary>
public interface IAnalysisRescoreRepository
{
    /// <summary>
    /// True when ANY [AnalysisApprovalVotes] row exists for the analysis — an approval or a veto.
    /// </summary>
    /// <remarks>
    /// Deliberately not on IAnalysisApprovalVoteRepository, which returns voter ids so the quorum can
    /// intersect them with the eligible managers. This is a different question: not "how many count",
    /// but "has anybody signed at all", and a deactivated manager's vote answers it too.
    /// </remarks>
    Task<bool> HasAnyVoteAsync(Guid analysisId, string tenantId, CancellationToken ct);

    /// <summary>
    /// The AnalysisId of result <paramref name="resultId"/> in this tenant, or null. A key-only read —
    /// the history endpoint needs nothing else off the row.
    /// </summary>
    Task<Guid?> GetAnalysisIdAsync(Guid resultId, string tenantId, CancellationToken ct);

    /// <summary>
    /// The AnalysisId and the BalanceJson of result <paramref name="resultId"/> in this tenant, or null —
    /// what the balance-correction history reads (the <c>origin</c> of each correction is on the trail).
    /// </summary>
    Task<(Guid AnalysisId, string? BalanceJson)?> GetCorrectionSourceAsync(Guid resultId, string tenantId, CancellationToken ct);

    /// <summary>The re-scores of one analysis, newest first. Empty when it was never re-scored.</summary>
    Task<IReadOnlyList<AnalysisRescoreHistoryItem>> GetHistoryAsync(
        Guid analysisId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Applies one re-score: re-checks the decision gate, updates the result's re-scored columns with
    /// <c>UPDATE … WHERE ResultId = @id AND RescoreCount = @expected</c> and inserts the history row —
    /// all in ONE transaction, so either both land or neither does.
    /// </summary>
    Task<AnalysisRescoreWriteOutcome> TryApplyAsync(AnalysisRescoreWrite write, CancellationToken ct);
}
