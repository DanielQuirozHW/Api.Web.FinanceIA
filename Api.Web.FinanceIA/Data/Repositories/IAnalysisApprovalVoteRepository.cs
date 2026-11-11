// Project : Api.Web.FinanceIA
// Created : 2026-09-16

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Data access for [dbo].[AnalysisApprovalVotes] (DDL 067) — the per-manager signatures the quorum
/// engine counts against the analyst's team's <see cref="Team.ApprovalQuorum"/>.
/// </summary>
/// <remarks>
/// Every method is scoped by tenant. <see cref="FinanciaDbContext"/> has no global query filter, so
/// the predicate is written by hand on every query — including the lookups keyed by AnalysisId,
/// which is globally unique and would otherwise reach an analysis belonging to another tenant.
///
/// <para>
/// Note what is NOT here: there is no "count the votes" method. The tally is not a property of this
/// table alone — a vote only counts if its caster is STILL an eligible active manager — so this
/// repository returns the voter ids and the caller intersects them with the eligible set it
/// resolved. That keeps a deactivated manager's signature on the record (an auditor asks for those)
/// while dropping it from the arithmetic.
/// </para>
/// </remarks>
public interface IAnalysisApprovalVoteRepository
{
    /// <summary>
    /// Records one manager's vote on one analysis.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the vote was inserted; <c>false</c> when this manager had ALREADY voted on
    /// this analysis. A repeat vote is not a second vote — the caller must not treat <c>false</c> as
    /// a failure, and must not let it raise the tally.
    /// </returns>
    Task<bool> RecordAsync(AnalysisApprovalVote vote, CancellationToken ct);

    /// <summary>
    /// Withdraws one manager's own vote on one analysis.
    /// </summary>
    /// <remarks>
    /// Scoped by (analysisId, userId, tenantId) so it can only ever reach the caller's own row.
    /// Nobody can withdraw somebody else's signature, by construction rather than by a check.
    /// </remarks>
    /// <returns><c>false</c> when there was nothing of theirs to withdraw.</returns>
    Task<bool> WithdrawAsync(Guid analysisId, Guid userId, string tenantId, CancellationToken ct);

    /// <summary>
    /// The distinct user ids holding an <see cref="AnalysisVoteValues.Approved"/> vote on this
    /// analysis. Seeks on UQ_AnalysisApprovalVotes_Analysis_User, which leads with AnalysisId.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetApproverUserIdsAsync(Guid analysisId, string tenantId, CancellationToken ct);

    /// <summary>
    /// <see cref="GetApproverUserIdsAsync"/> for many analyses in ONE query: the same predicate
    /// (tenant, <see cref="AnalysisVoteValues.Approved"/>, distinct per analysis), keyed by AnalysisId.
    /// An analysis with no approval is absent from the map. Read-only.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetApproverUserIdsByAnalysesAsync(
        IReadOnlyCollection<Guid> analysisIds, string tenantId, CancellationToken ct);

    /// <summary>
    /// Deletes every vote on one analysis, resetting the tally to zero.
    /// </summary>
    /// <remarks>
    /// Called when an analysis ENTERS 'PreApproved' and when it is returned to review. Returning it
    /// for rework is the rule that names this explicitly, but it is not the only way back into
    /// 'PreApproved' — a rejected or approved analysis can be re-decided — and votes surviving that
    /// would silently pre-fill the new quorum with signatures given for a superseded proposal.
    /// </remarks>
    /// <returns>How many votes were discarded.</returns>
    Task<int> DeleteByAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct);
}
