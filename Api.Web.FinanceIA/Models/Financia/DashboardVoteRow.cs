// Project : Api.Web.FinanceIA
// Created : 2026-10-23

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>One of a user's [dbo].[AnalysisApprovalVotes] rows, projected for the approvals dashboard.</summary>
/// <param name="AnalysisId">The analysis voted on.</param>
/// <param name="Vote"><see cref="AnalysisVoteValues.Approved"/> or <see cref="AnalysisVoteValues.Rejected"/>.</param>
/// <param name="VotedAt">When the vote was cast (UTC).</param>
public sealed record DashboardVoteRow(Guid AnalysisId, string Vote, DateTime VotedAt);
