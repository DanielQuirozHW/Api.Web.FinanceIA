// Project : Api.Web.FinanceIA
// Created : 2026-09-16

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[AnalysisApprovalVotes] (DDL 067).
/// One row per manager per analysis, recording how that manager voted on a pre-approval.
/// </summary>
/// <remarks>
/// This is the table the quorum engine counts against <see cref="Team.ApprovalQuorum"/>. DDL 067
/// provisioned it in the Phase 1 batch and left it deliberately unmapped; this entity is what Phase 2
/// adds, and mapping it is what brings the table under the <c>--verify-schema</c> deploy gate.
///
/// <para>
/// THE KEY IS <see cref="AnalysisId"/>, NOT ResultId — and the route does the opposite, so this is
/// worth being explicit about. <c>PATCH /api/analyses/{id}/decision</c> takes a RESULT id and hops to
/// the analysis through it, but the decision state machine (Status, PreApprovedBy, ReviewedBy) lives
/// entirely on [dbo].[Analyses]. A vote is a signature on that state machine, so it keys on the row
/// that carries it. Keying on ResultId would let a re-scored analysis with a second result row
/// collect two independent quorums.
/// </para>
///
/// <para>
/// <see cref="Vote"/> carries no CHECK constraint in the database, by DDL 067's explicit choice: the
/// vocabulary is validated here in the API instead, so admitting a new value later costs no ALTER on
/// every tenant database. <see cref="AnalysisVoteValues"/> is that vocabulary.
/// </para>
///
/// <para>
/// One manager, one vote per analysis, enforced structurally by
/// UQ_AnalysisApprovalVotes_Analysis_User rather than by an application read — the failure mode is a
/// race (two browser tabs, a retried request), and a "have they voted already?" check cannot close
/// that window.
/// </para>
/// </remarks>
public sealed class AnalysisApprovalVote
{
    /// <summary>Primary key. Supplied by the application — see AnalysisApprovalVoteConfiguration.</summary>
    public Guid VoteId { get; set; }

    /// <summary>The analysis being voted on. [dbo].[Analyses].AnalysisId — NOT a ResultId.</summary>
    public Guid AnalysisId { get; set; }

    /// <summary>The manager who cast this vote. [dbo].[Users].UserId.</summary>
    public Guid UserId { get; set; }

    /// <summary>Tenant that owns this row. Firebase tenant slug, not a GUID.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>How they voted. One of <see cref="AnalysisVoteValues"/>.</summary>
    public string Vote { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the vote was cast. Always supplied explicitly by the application.</summary>
    public DateTime VotedAt { get; set; }
}
