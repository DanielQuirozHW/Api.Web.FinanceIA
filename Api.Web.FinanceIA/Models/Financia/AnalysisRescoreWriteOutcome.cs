// Project : Api.Web.FinanceIA
// Created : 2026-10-22

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>How <c>IAnalysisRescoreRepository.TryApplyAsync</c> ended. Only <see cref="Applied"/> wrote anything.</summary>
public enum AnalysisRescoreWriteOutcome
{
    /// <summary>The result was updated and the history row inserted, in one transaction.</summary>
    Applied,

    /// <summary>RescoreCount was no longer the value read: another re-score landed first.</summary>
    LostRace,

    /// <summary>The analysis left PendingReview while the re-score was computing.</summary>
    NotPending,

    /// <summary>A vote, a pre-approval or a decision was recorded while the re-score was computing.</summary>
    HasVotes
}
