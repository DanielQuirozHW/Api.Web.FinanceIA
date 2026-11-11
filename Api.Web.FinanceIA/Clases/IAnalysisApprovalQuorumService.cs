// Project : Api.Web.FinanceIA
// Created : 2026-09-16

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// How a confirmation attempt ended.
/// </summary>
public enum ApprovalVoteOutcome
{
    /// <summary>The vote was recorded and the analysis is still waiting for more signatures.</summary>
    Pending = 0,

    /// <summary>This vote crossed the quorum and THIS call wrote the final approved record.</summary>
    Confirmed = 1,

    /// <summary>
    /// The analysis was already settled by another decider between this call's tally and its write.
    /// The vote is recorded and the analysis IS approved — but on somebody else's signature, so the
    /// caller must not claim the confirmation, the audit row or the notification as its own.
    /// </summary>
    AlreadyConfirmed = 2
}

/// <summary>
/// Where a pre-approval stands: how many of the required signatures it has collected.
/// </summary>
/// <param name="Required">
/// The EFFECTIVE quorum: the team's <see cref="Team.ApprovalQuorum"/> capped by how many eligible
/// managers the analyst's team actually has, and never below 1. The cap is what stops a quorum of 3
/// from stranding a team that has two managers; the floor is what stops a 0 in the column — which
/// DDL 065 permits, having shipped no CHECK constraint — from confirming on no signatures at all.
/// </param>
/// <param name="ApproverUserIds">
/// The distinct approvals that COUNT: votes held by users who are still active and still hold a role
/// that can confirm. A signature from someone since deactivated stays on the record but leaves this
/// list, which is what makes "a vote by a now-inactive manager does not count" true by construction.
/// </param>
/// <param name="InForce">
/// Whether a quorum is actually binding — the analyst's team has at least one eligible manager. When
/// false the quorum degenerates to 1 and the behaviour is byte-for-byte what it was before this
/// service existed. The Portal uses it to decide whether to show a tally at all.
/// </param>
public sealed record ApprovalTally(
    int                 Required,
    IReadOnlyList<Guid> ApproverUserIds,
    bool                InForce)
{
    /// <summary>How many signatures are on record and counting.</summary>
    public int Approved => ApproverUserIds.Count;

    /// <summary>Whether the analysis has collected enough signatures to be committed.</summary>
    public bool IsSatisfied => Approved >= Required;
}

/// <summary>
/// The team-quorum engine: turns a single confirmation into a VOTE, and decides when enough votes
/// have been collected to commit the credit.
/// </summary>
/// <remarks>
/// <para>
/// WHAT THE TEAM DECIDES IS HOW MANY, NOT WHO. The effective quorum is sized from the analyst's
/// team's eligible managers, but any active confirmer's approval counts toward it. Sizing and
/// eligibility were separated deliberately: making the team's own managers the only valid voters
/// would let a team whose managers are on holiday strand an analysis with no escape hatch, and
/// exempting outsiders from the quorum instead would let any RiskManager in the tenant settle a
/// two-signature analysis alone — which is the exact failure this feature exists to prevent.
/// </para>
///
/// <para>
/// It engages only when the analyst's team has eligible managers. A team with none — or an analyst
/// with no team at all — yields an effective quorum of 1, so the first confirmer confirms and
/// nothing about the endpoint's behaviour changes.
/// </para>
///
/// <para>
/// IT FAILS LOUD, unlike <see cref="AnalysisDecisionNotifier"/> beside it. A notifier that throws
/// costs an inbox copy; a quorum engine that swallowed its own failure would confirm a credit
/// decision on one signature when the tenant configured two. Every failure here propagates and the
/// decision is refused.
/// </para>
/// </remarks>
public interface IAnalysisApprovalQuorumService
{
    /// <summary>
    /// Records the caller's approval and confirms the analysis if that crossed the quorum.
    /// </summary>
    /// <remarks>
    /// On <see cref="ApprovalVoteOutcome.Confirmed"/> the final record has ALREADY been persisted by
    /// this call and <paramref name="analysis"/> is updated in place to match, so the caller's
    /// response, audit row and notification all describe the committed state. On
    /// <see cref="ApprovalVoteOutcome.AlreadyConfirmed"/> it is refreshed from the database instead.
    /// </remarks>
    Task<(ApprovalVoteOutcome Outcome, ApprovalTally Tally)> VoteAndTryConfirmAsync(
        string            tenantId,
        Analysis          analysis,
        User              voter,
        decimal?          decisionAmount,
        string?           decisionCurrency,
        string?           confirmedOperationsJson,
        string?           reviewNotes,
        DateTime          nowUtc,
        CancellationToken ct);

    /// <summary>
    /// Retracts the caller's own standing approval.
    /// </summary>
    /// <returns>
    /// Whether a vote was actually removed, and the tally that results. <c>false</c> is an ordinary
    /// outcome — the caller had nothing on record — not an error.
    /// </returns>
    Task<(bool Withdrawn, ApprovalTally Tally)> WithdrawAsync(
        string tenantId, Analysis analysis, User voter, CancellationToken ct);

    /// <summary>
    /// Records a veto for the trail. The rejection itself is written by the caller — a veto is
    /// immediate and answers to no quorum, so nothing decided here.
    /// </summary>
    Task RecordVetoAsync(
        string tenantId, Analysis analysis, User voter, DateTime nowUtc, CancellationToken ct);

    /// <summary>
    /// Discards every vote on an analysis, resetting the tally to zero.
    /// </summary>
    Task DiscardVotesAsync(string tenantId, Guid analysisId, CancellationToken ct);

    /// <summary>
    /// Reads the current tally without casting anything, for a response whose request did not vote.
    /// </summary>
    Task<ApprovalTally> GetTallyAsync(
        string tenantId, Analysis analysis, CancellationToken ct);

    /// <summary>
    /// <see cref="GetTallyAsync"/> for many analyses at once, keyed by <see cref="Analysis.AnalysisId"/>:
    /// the same effective quorum and the same counting approvals, read in a fixed number of queries
    /// whatever the batch size. Read-only — it casts, withdraws and confirms nothing, and the decision
    /// path never calls it.
    /// </summary>
    /// <remarks>
    /// Each analysis needs <see cref="Analysis.AnalysisId"/> and <see cref="Analysis.PreApprovedBy"/>
    /// only; the quorum is sized from the PRE-APPROVER's team, exactly as for a single analysis.
    /// </remarks>
    Task<IReadOnlyDictionary<Guid, ApprovalTally>> GetTalliesAsync(
        string tenantId, IReadOnlyCollection<Analysis> analyses, CancellationToken ct);
}
