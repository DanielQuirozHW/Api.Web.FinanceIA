// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-07

using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Xunit;

namespace Api.Web.FinanceIA.Tests.AnalysisRuns;

/// <summary>
/// Covers <see cref="AnalysisRunFailureReasons"/>: every stored code maps to its failureReason and
/// causeGroup, anything unknown is other/ours, both vocabularies are closed, and the attempts-left
/// arithmetic. Also pins that failureCode (<see cref="AnalysisRunFailureCodes.FromStoredReason"/>) did
/// not move for the same inputs.
/// </summary>
public sealed class AnalysisRunFailureReasonsTests
{
    [Theory]
    [InlineData("claude_no_financial_statements", "no_financial_statements", "document", "model_error")]
    [InlineData("claude_refusal",                 "unreadable_document",     "document", "model_error")]
    [InlineData("claude_unreadable_answer",       "unreadable_document",     "document", "model_error")]
    [InlineData("claude_api_error",               "file_rejected",           "document", "model_error")]
    [InlineData("claude_timeout",                 "timeout",                 "ours",     "model_error")]
    [InlineData("claude_server_error",            "service_unavailable",     "ours",     "model_error")]
    [InlineData("claude_network_error",           "service_unavailable",     "ours",     "model_error")]
    [InlineData("claude_rate_limited",            "service_unavailable",     "ours",     "quota")]
    [InlineData("claude_auth_error",              "service_unavailable",     "ours",     "model_error")]
    [InlineData("claude_unparseable_answer",      "reading_failed",          "ours",     "model_error")]
    [InlineData("claude_max_tokens",              "reading_failed",          "ours",     "model_error")]
    [InlineData("stale_processing",               "interrupted",             "ours",     "interrupted")]
    [InlineData("TaskCanceledException",          "interrupted",             "ours",     "interrupted")]
    [InlineData("OperationCanceledException",     "interrupted",             "ours",     "interrupted")]
    [InlineData("consent_required",               "consent_required",        "ours",     "consent_required")]
    [InlineData("claude_some_future_code",        "other",                   "ours",     "model_error")]
    [InlineData("DbUpdateException",              "other",                   "ours",     "other")]
    [InlineData("CLAUDE_TIMEOUT",                 "other",                   "ours",     "other")] // ordinal, no guessing
    [InlineData("something new",                  "other",                   "ours",     "other")]
    [InlineData("",                               "other",                   "ours",     "other")]
    [InlineData("   ",                            "other",                   "ours",     "other")]
    [InlineData(null,                             "other",                   "ours",     "other")]
    public void EveryStoredReason_MapsToItsReasonAndGroup_AndFailureCodeIsUnchanged(
        string? stored, string expectedReason, string expectedGroup, string expectedFailureCode)
    {
        var reason = AnalysisRunFailureReasons.FromStoredReason(stored);

        Assert.Equal(expectedReason, reason);
        Assert.Equal(expectedGroup, AnalysisRunFailureReasons.CauseGroupOf(reason));
        Assert.Equal(expectedFailureCode, AnalysisRunFailureCodes.FromStoredReason(stored));
    }

    [Fact]
    public void EveryStoredConstant_HasADedicatedReason()
    {
        string[] stored =
        [
            AnalysisRunFailureCodes.StoredClaudeApiError, AnalysisRunFailureCodes.StoredClaudeRateLimited,
            AnalysisRunFailureCodes.StoredClaudeTimeout, AnalysisRunFailureCodes.StoredClaudeNetworkError,
            AnalysisRunFailureCodes.StoredClaudeServerError, AnalysisRunFailureCodes.StoredClaudeAuthError,
            AnalysisRunFailureCodes.StoredClaudeRefusal, AnalysisRunFailureCodes.StoredClaudeUnreadableAnswer,
            AnalysisRunFailureCodes.StoredClaudeUnparseableAnswer, AnalysisRunFailureCodes.StoredClaudeMaxTokens,
            AnalysisRunFailureCodes.StoredClaudeNoFinancialStatements, AnalysisRunFailureCodes.StoredStaleProcessing,
            AnalysisRunFailureCodes.StoredConsentRequired
        ];

        foreach (var code in stored)
            Assert.NotEqual(AnalysisRunFailureReasons.Other, AnalysisRunFailureReasons.FromStoredReason(code));
    }

    [Fact]
    public void TheVocabularies_AreClosed()
    {
        Assert.Equal(
            ["consent_required", "file_rejected", "interrupted", "no_financial_statements", "other",
             "reading_failed", "service_unavailable", "timeout", "unreadable_document"],
            AnalysisRunFailureReasons.All.Order(StringComparer.Ordinal));
        Assert.Equal(["document", "ours"], AnalysisRunFailureReasons.CauseGroups.Order(StringComparer.Ordinal));
        Assert.Equal(
            ["file_rejected", "no_financial_statements", "unreadable_document"],
            AnalysisRunFailureReasons.DocumentCaused.Order(StringComparer.Ordinal));
        Assert.Subset(AnalysisRunFailureReasons.All.ToHashSet(), AnalysisRunFailureReasons.DocumentCaused.ToHashSet());

        foreach (var reason in AnalysisRunFailureReasons.All)
            Assert.Contains(AnalysisRunFailureReasons.CauseGroupOf(reason), AnalysisRunFailureReasons.CauseGroups);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not_a_reason")]
    public void CauseGroupOf_AnythingOutsideTheDocumentList_IsOurs(string? reason)
        => Assert.Equal(AnalysisRunFailureReasons.Ours, AnalysisRunFailureReasons.CauseGroupOf(reason));

    [Theory]
    [InlineData(2, 0, 2)]
    [InlineData(2, 1, 1)]
    [InlineData(2, 2, 0)]
    [InlineData(2, 3, 0)]   // never negative
    [InlineData(0, 1, null)]   // budget off
    [InlineData(-1, 0, null)]  // budget off
    [InlineData(2, null, null)] // attempts unknown
    public void AttemptsRemaining_IsTheBudgetLeft_OrNull(int max, int? spent, int? expected)
        => Assert.Equal(expected, AnalysisRunFailureReasons.AttemptsRemaining(max, spent));

    [Theory]
    [InlineData("abc", 1)]
    [InlineData("ABC", 1)]   // the repository's dictionary is case-insensitive
    [InlineData("zzz", 2)]   // no row summed for this hash: nothing spent
    [InlineData("", null)]   // a blank hash is never a match
    [InlineData(null, null)]
    public void AttemptsRemainingFor_ReadsTheBatchedSums(string? hash, int? expected)
    {
        var sums = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["abc"] = 1 };

        Assert.Equal(expected, AnalysisRunListService.AttemptsRemainingFor(hash, sums, maxAttempts: 2));
    }

    [Fact]
    public void AttemptsRemainingFor_SumsThatCouldNotBeRead_IsNull()
        => Assert.Null(AnalysisRunListService.AttemptsRemainingFor("abc", null, maxAttempts: 2));
}
