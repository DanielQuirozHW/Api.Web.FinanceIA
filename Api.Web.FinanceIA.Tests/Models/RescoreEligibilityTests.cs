// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-22

using Api.Web.FinanceIA.Models.Financia;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Models;

/// <summary>
/// The rules shared by POST {id}/rescore and the detail's canRescore: their order, and that every
/// errorCode has its own Spanish detail.
/// </summary>
public sealed class RescoreEligibilityTests
{
    private static Analysis Pending() => new()
    {
        AnalysisId   = Guid.NewGuid(),
        AnalysisKind = AnalysisKinds.Balance,
        Status       = AnalysisStatuses.PendingReview
    };

    [Fact]
    public void A_pending_balance_analysis_with_a_balance_and_an_idle_document_may_be_rescored()
    {
        Assert.Null(RescoreEligibility.Evaluate(Pending(), hasBalance: true, hasVotes: false, "Completed"));
        Assert.Null(RescoreEligibility.Evaluate(Pending(), hasBalance: true, hasVotes: false, null));
    }

    [Fact]
    public void A_pending_persona_analysis_may_be_re_evaluated_without_a_balance_or_a_document()
    {
        var persona = Pending();
        persona.AnalysisKind = AnalysisKinds.PersonaHumana;

        // Neither the missing balance nor a document status is a persona rule.
        Assert.Null(RescoreEligibility.Evaluate(persona, hasBalance: false, hasVotes: false, null));
        Assert.Null(RescoreEligibility.Evaluate(persona, hasBalance: false, hasVotes: false, "Processing"));
    }

    [Fact]
    public void A_persona_analysis_is_held_to_the_decision_rules_only()
    {
        var decided = Pending();
        decided.AnalysisKind = AnalysisKinds.PersonaHumana;
        decided.Status       = AnalysisStatuses.Approved;
        Assert.Equal(RescoreErrorCodes.NotPending,
            RescoreEligibility.Evaluate(decided, hasBalance: false, hasVotes: true, "Processing"));

        var preApproved = Pending();
        preApproved.AnalysisKind  = AnalysisKinds.PersonaHumana;
        preApproved.PreApprovedBy = Guid.NewGuid();
        Assert.Equal(RescoreErrorCodes.HasVotes,
            RescoreEligibility.Evaluate(preApproved, hasBalance: false, hasVotes: false, null));

        var voted = Pending();
        voted.AnalysisKind = AnalysisKinds.PersonaHumana;
        Assert.Equal(RescoreErrorCodes.HasVotes,
            RescoreEligibility.Evaluate(voted, hasBalance: false, hasVotes: true, null));
    }

    [Fact]
    public void A_missing_balance_is_reported_before_the_status()
    {
        var analysis = Pending();
        analysis.Status = AnalysisStatuses.Approved;

        Assert.Equal(RescoreErrorCodes.ResultMissing,
            RescoreEligibility.Evaluate(analysis, hasBalance: false, hasVotes: false, null));
    }

    [Fact]
    public void Status_then_decision_record_then_document()
    {
        var approved = Pending();
        approved.Status = AnalysisStatuses.Approved;
        Assert.Equal(RescoreErrorCodes.NotPending, RescoreEligibility.Evaluate(approved, true, true, "Processing"));

        Assert.Equal(RescoreErrorCodes.HasVotes, RescoreEligibility.Evaluate(Pending(), true, true, "Processing"));

        Assert.Equal(RescoreErrorCodes.Processing, RescoreEligibility.Evaluate(Pending(), true, false, "Processing"));
    }

    [Theory]
    [InlineData(true,  false, false)]
    [InlineData(false, true,  false)]
    [InlineData(false, false, true)]
    public void Any_signature_on_the_row_counts_as_a_decision_record(bool preApprovedBy, bool preApprovedAt, bool reviewedBy)
    {
        var analysis = Pending();
        if (preApprovedBy) analysis.PreApprovedBy = Guid.NewGuid();
        if (preApprovedAt) analysis.PreApprovedAt = DateTime.UtcNow;
        if (reviewedBy)    analysis.ReviewedBy    = Guid.NewGuid();

        Assert.True(RescoreEligibility.HasDecisionRecord(analysis));
        Assert.Equal(RescoreErrorCodes.HasVotes, RescoreEligibility.Evaluate(analysis, true, false, null));
    }

    [Theory]
    [InlineData(RescoreErrorCodes.PersonAnalysis)]
    [InlineData(RescoreErrorCodes.ResultMissing)]
    [InlineData(RescoreErrorCodes.NotPending)]
    [InlineData(RescoreErrorCodes.HasVotes)]
    [InlineData(RescoreErrorCodes.Processing)]
    [InlineData(RescoreErrorCodes.InProgress)]
    [InlineData(RescoreErrorCodes.BcraUnavailable)]
    [InlineData(RescoreErrorCodes.Failed)]
    public void Every_errorCode_has_its_own_detail(string errorCode)
    {
        Assert.NotEqual(RescoreEligibility.Detail("unknown"), RescoreEligibility.Detail(errorCode));
    }
}
