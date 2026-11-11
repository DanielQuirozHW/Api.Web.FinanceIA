// Project : Api.Web.FinanceIA
// Created : 2026-09-16

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The vocabulary of <see cref="AnalysisApprovalVote.Vote"/>.
/// </summary>
/// <remarks>
/// These live in code rather than in a CHECK constraint, and that is DDL 067's deliberate choice:
/// [dbo].[Analyses] HAS such a constraint (CK_Analyses_Status) and script 060 exists for no other
/// purpose than to ALTER it on every tenant database so it would admit one single new value. Freezing
/// a vocabulary into the schema costs a visit to N databases; keeping it here costs a deploy.
/// </remarks>
public static class AnalysisVoteValues
{
    /// <summary>The manager signed off. Counted toward the team's quorum.</summary>
    public const string Approved = "Approved";

    /// <summary>The manager vetoed. Terminal on its own — no quorum applies to a rejection.</summary>
    public const string Rejected = "Rejected";
}
