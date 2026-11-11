// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-16

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// A scripted <see cref="IAnalysisApprovalQuorumService"/> for the controller suites.
/// </summary>
/// <remarks>
/// Shared rather than nested, which is the exception in this project, because three suites need it
/// and the alternative is the same sixty lines three times: <c>AnalysisControllerQuorumTests</c> drives
/// it, <c>AnalysisControllerDecideTests</c> needs a no-quorum one so its pre-existing assertions keep
/// meaning what they meant, and <c>AnalysisControllerGetByIdTests</c> needs one because the detail
/// endpoint now reads a tally. <c>AuthorizationTestHelpers</c> in this folder is the precedent.
///
/// <para>
/// SCRIPTED, NOT SIMULATED. It returns the outcome the test sets and records what it was asked, so a
/// controller test asserts on what the CONTROLLER does with an outcome. The engine's own arithmetic —
/// the cap, the floor, the eligibility intersection — is proven against real repositories in
/// <c>Clases/AnalysisApprovalQuorumServiceTests</c>, where a stub could not lie about it.
/// </para>
///
/// <para>
/// <see cref="NoQuorum"/> is the important default: it reproduces a tenant whose teams name no
/// managers, which is the state every existing test in this repository was written against.
/// </para>
/// </remarks>
internal sealed class StubAnalysisApprovalQuorumService : IAnalysisApprovalQuorumService
{
    /// <summary>What the next <see cref="VoteAndTryConfirmAsync"/> reports.</summary>
    public ApprovalVoteOutcome Outcome { get; set; } = ApprovalVoteOutcome.Confirmed;

    /// <summary>The tally every method returns.</summary>
    public ApprovalTally Tally { get; set; } = NoQuorum();

    /// <summary>Whether the next withdraw removed anything.</summary>
    public bool WithdrawRemoves { get; set; } = true;

    public int VoteCallCount     { get; private set; }
    public int WithdrawCallCount { get; private set; }
    public int VetoCallCount     { get; private set; }
    public int DiscardCallCount  { get; private set; }
    public int TallyCallCount    { get; private set; }

    /// <summary>The analysis ids passed to <see cref="DiscardVotesAsync"/>, in order.</summary>
    public List<Guid> Discarded { get; } = [];

    /// <summary>No quorum in force — one signature settles it, exactly as before the engine shipped.</summary>
    public static ApprovalTally NoQuorum() => new(Required: 1, ApproverUserIds: [], InForce: false);

    /// <summary>A quorum of <paramref name="required"/> with <paramref name="approvers"/> on record.</summary>
    public static ApprovalTally Quorum(int required, params Guid[] approvers)
        => new(required, approvers, InForce: true);

    public Task<(ApprovalVoteOutcome Outcome, ApprovalTally Tally)> VoteAndTryConfirmAsync(
        string tenantId, Analysis analysis, User voter,
        decimal? decisionAmount, string? decisionCurrency, string? confirmedOperationsJson,
        string? reviewNotes, DateTime nowUtc, CancellationToken ct)
    {
        VoteCallCount++;

        // The real service writes the final record itself through the guarded UPDATE and mirrors it
        // onto the entity, because ExecuteUpdateAsync bypasses the change tracker. Reproducing that
        // here is what lets a controller test assert the response and the audit row describe a
        // COMMITTED decision while the analysis repository's UpdateAsync was never called.
        if (Outcome == ApprovalVoteOutcome.Confirmed)
        {
            analysis.Status                  = AnalysisStatuses.Approved;
            analysis.ReviewedBy              = voter.UserId;
            analysis.ReviewedAt              = nowUtc;
            analysis.DecisionAmount          = decisionAmount;
            analysis.DecisionCurrency        = decisionCurrency;
            analysis.ConfirmedOperationsJson = confirmedOperationsJson;
            if (reviewNotes is not null) analysis.ReviewNotes = reviewNotes;
        }

        return Task.FromResult((Outcome, Tally));
    }

    public Task<(bool Withdrawn, ApprovalTally Tally)> WithdrawAsync(
        string tenantId, Analysis analysis, User voter, CancellationToken ct)
    {
        WithdrawCallCount++;
        return Task.FromResult((WithdrawRemoves, Tally));
    }

    public Task RecordVetoAsync(
        string tenantId, Analysis analysis, User voter, DateTime nowUtc, CancellationToken ct)
    {
        VetoCallCount++;
        return Task.CompletedTask;
    }

    public Task DiscardVotesAsync(string tenantId, Guid analysisId, CancellationToken ct)
    {
        DiscardCallCount++;
        Discarded.Add(analysisId);
        return Task.CompletedTask;
    }

    public Task<ApprovalTally> GetTallyAsync(string tenantId, Analysis analysis, CancellationToken ct)
    {
        TallyCallCount++;
        return Task.FromResult(Tally);
    }

    public Task<IReadOnlyDictionary<Guid, ApprovalTally>> GetTalliesAsync(
        string tenantId, IReadOnlyCollection<Analysis> analyses, CancellationToken ct)
    {
        TallyCallCount++;
        IReadOnlyDictionary<Guid, ApprovalTally> tallies = analyses
            .Select(a => a.AnalysisId)
            .Distinct()
            .ToDictionary(id => id, _ => Tally);
        return Task.FromResult(tallies);
    }
}
