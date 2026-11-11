// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Xunit;

namespace Api.Web.FinanceIA.Tests.AnalysisRuns;

/// <summary>
/// Covers <see cref="AnalysisRunFailureCodes"/> and <see cref="AnalysisRunListService.ResolveFailure"/>:
/// every stored reason maps into the closed vocabulary, anything unknown is "other", and a reason left
/// by an EARLIER failure is never read as this one's.
/// </summary>
public sealed class AnalysisRunFailureCodesTests
{
    private static readonly DateTime FailedAt = new(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("stale_processing",           AnalysisRunFailureCodes.Interrupted)]
    [InlineData("TaskCanceledException",      AnalysisRunFailureCodes.Interrupted)]
    [InlineData("OperationCanceledException", AnalysisRunFailureCodes.Interrupted)]
    [InlineData("consent_required",           AnalysisRunFailureCodes.ConsentRequired)]
    [InlineData("claude_rate_limited",        AnalysisRunFailureCodes.Quota)]
    [InlineData("claude_api_error",           AnalysisRunFailureCodes.ModelError)]
    [InlineData("claude_timeout",             AnalysisRunFailureCodes.ModelError)]   // a future claude_* code
    [InlineData("claude_no_financial_statements", AnalysisRunFailureCodes.ModelError)] // no contract change on GET /runs
    [InlineData("DbUpdateException",          AnalysisRunFailureCodes.Other)]
    [InlineData("NullReferenceException",     AnalysisRunFailureCodes.Other)]
    [InlineData("document_deleted",           AnalysisRunFailureCodes.Other)]
    [InlineData("legajo_document",            AnalysisRunFailureCodes.Other)]
    [InlineData("STALE_PROCESSING",           AnalysisRunFailureCodes.Other)]        // ordinal, no guessing
    [InlineData("something new",              AnalysisRunFailureCodes.Other)]
    [InlineData("",                           AnalysisRunFailureCodes.Other)]
    [InlineData("   ",                        AnalysisRunFailureCodes.Other)]
    [InlineData(null,                         AnalysisRunFailureCodes.Other)]
    public void FromStoredReason_MapsIntoTheVocabulary(string? stored, string expected)
    {
        var code = AnalysisRunFailureCodes.FromStoredReason(stored);

        Assert.Equal(expected, code);
        Assert.Contains(code, AnalysisRunFailureCodes.All);
    }

    [Fact]
    public void TheVocabulary_IsExactlyTheDocumentedOne()
    {
        Assert.Equal(
            ["consent_required", "interrupted", "model_error", "other", "quota"],
            AnalysisRunFailureCodes.All.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheWritersUseTheMappedCodes()
    {
        // The pipeline, the abandon and the sweep write these constants; the mapping reads them.
        Assert.Equal("claude_api_error", AnalysisRunFailureCodes.StoredClaudeApiError);
        Assert.Equal("claude_rate_limited", AnalysisRunFailureCodes.StoredClaudeRateLimited);
        Assert.Equal("stale_processing", AnalysisRunFailureCodes.StoredStaleProcessing);
        Assert.Equal("consent_required", AnalysisRunFailureCodes.StoredConsentRequired);
        Assert.Equal("Analysis.Failed", AnalysisRunFailureCodes.FailedAuditAction);
    }

    [Theory]
    [InlineData("""{"reason":"stale_processing"}""", "stale_processing")]
    [InlineData("""{"reason":"consent_required","extra":1}""", "consent_required")]
    [InlineData("""{"reason":42}""", null)]
    [InlineData("""{"other":"x"}""", null)]
    [InlineData("""["reason"]""", null)]
    [InlineData("""{"reason":""}""", null)]
    [InlineData("not json", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ReadAuditReason_ReadsOnlyAStringReason(string? json, string? expected)
        => Assert.Equal(expected, AnalysisRunFailureCodes.ReadAuditReason(json));

    [Fact]
    public void ReadAuditReason_IgnoresAnOverlongReason()
        => Assert.Null(AnalysisRunFailureCodes.ReadAuditReason($$"""{"reason":"{{new string('x', 101)}}"}"""));

    // ── Which reason belongs to THIS failure ─────────────────────────────────────

    [Fact]
    public void ResolveFailure_TheDocumentCodeOfThisFailure_Wins()
    {
        var document = Failed(lastError: "claude_rate_limited", lastFailedAt: FailedAt.AddMilliseconds(5));
        var audit    = Audit(FailedAt.AddMilliseconds(10), """{"reason":"stale_processing"}""");

        var (code, _) = AnalysisRunListService.ResolveFailure(document, [audit]);

        Assert.Equal(AnalysisRunFailureCodes.Quota, code);
    }

    [Fact]
    public void ResolveFailure_AnEarlierFailuresDocumentCode_IsIgnored_ForTheAuditReason()
    {
        // The model failed yesterday; today's retry was released by the stale sweep, which writes no
        // document code. The column still says claude_api_error — it is not today's reason.
        var document = Failed(lastError: "claude_api_error", lastFailedAt: FailedAt.AddDays(-1));
        var audit    = Audit(FailedAt.AddMilliseconds(20), """{"reason":"stale_processing"}""");

        var (code, requestedBy) = AnalysisRunListService.ResolveFailure(document, [audit]);

        Assert.Equal(AnalysisRunFailureCodes.Interrupted, code);
        Assert.Null(requestedBy);   // the sweep has no requester
    }

    [Fact]
    public void ResolveFailure_AnEarlierFailuresAuditRow_IsIgnored()
    {
        var document = Failed(lastError: null, lastFailedAt: null);
        var stale    = Audit(FailedAt.AddHours(-3), """{"reason":"consent_required"}""", Guid.NewGuid());

        var (code, requestedBy) = AnalysisRunListService.ResolveFailure(document, [stale]);

        Assert.Equal(AnalysisRunFailureCodes.Other, code);
        Assert.Null(requestedBy);
    }

    [Fact]
    public void ResolveFailure_TheRequester_ComesFromThisFailuresAuditRow()
    {
        var requester = Guid.NewGuid();
        var document  = Failed(lastError: "claude_api_error", lastFailedAt: FailedAt.AddMilliseconds(3));
        var audit     = Audit(FailedAt.AddMilliseconds(8), newValueJson: null, requester);   // Claude path: no reason

        var (code, requestedBy) = AnalysisRunListService.ResolveFailure(document, [audit]);

        Assert.Equal(AnalysisRunFailureCodes.ModelError, code);
        Assert.Equal(requester, requestedBy);
    }

    [Fact]
    public void ResolveFailure_NoTraceAtAll_IsOther()
        => Assert.Equal(AnalysisRunFailureCodes.Other,
            AnalysisRunListService.ResolveFailure(Failed(lastError: null, lastFailedAt: null), null).Code);

    private static AnalysisRunDocumentProjection Failed(string? lastError, DateTime? lastFailedAt) => new()
    {
        DocumentId             = Guid.NewGuid(),
        EntityId               = Guid.NewGuid(),
        FileName               = "balance.pdf",
        ProcessedAt            = FailedAt,
        LastExtractionError    = lastError,
        LastExtractionFailedAt = lastFailedAt
    };

    private static AnalysisRunFailureAuditProjection Audit(DateTime timestamp, string? newValueJson, Guid? userId = null) => new()
    {
        DocumentId   = Guid.NewGuid(),
        Timestamp    = timestamp,
        UserId       = userId,
        NewValueJson = newValueJson
    };

    // ── The extraction-budget refusal's lastFailureCode ─────────────────────────

    [Theory]
    [InlineData("claude_refusal",           AnalysisRunFailureCodes.Unreadable)]
    [InlineData("claude_unreadable_answer", AnalysisRunFailureCodes.Unreadable)]
    [InlineData("claude_no_financial_statements", AnalysisRunFailureCodes.Unreadable)]
    [InlineData("claude_api_error",         AnalysisRunFailureCodes.ModelError)]
    [InlineData("claude_timeout",           AnalysisRunFailureCodes.ModelError)]
    [InlineData("claude_server_error",      AnalysisRunFailureCodes.ModelError)]
    [InlineData("claude_network_error",     AnalysisRunFailureCodes.ModelError)]
    [InlineData("claude_auth_error",        AnalysisRunFailureCodes.ModelError)]
    [InlineData("claude_max_tokens",        AnalysisRunFailureCodes.ModelError)]
    [InlineData("claude_rate_limited",      AnalysisRunFailureCodes.Quota)]
    [InlineData("stale_processing",         AnalysisRunFailureCodes.Interrupted)]
    [InlineData("TaskCanceledException",    AnalysisRunFailureCodes.Interrupted)]
    [InlineData("consent_required",         AnalysisRunFailureCodes.ConsentRequired)]
    [InlineData("DbUpdateException",        AnalysisRunFailureCodes.Other)]
    [InlineData("",                         AnalysisRunFailureCodes.Other)]
    [InlineData(null,                       AnalysisRunFailureCodes.Other)]
    public void ForBudgetRefusal_maps_a_stored_reason_to_the_closed_vocabulary(string? stored, string expected)
    {
        var code = AnalysisRunFailureCodes.ForBudgetRefusal(stored);

        Assert.Equal(expected, code);
        Assert.Contains(code, AnalysisRunFailureCodes.BudgetRefusalCodes);
    }

    [Fact]
    public void Unreadable_is_only_in_the_budget_refusal_vocabulary_not_in_GET_runs()
    {
        Assert.DoesNotContain(AnalysisRunFailureCodes.Unreadable, AnalysisRunFailureCodes.All);
        Assert.Equal(AnalysisRunFailureCodes.ModelError, AnalysisRunFailureCodes.FromStoredReason("claude_refusal"));
        Assert.Equal(AnalysisRunFailureCodes.ModelError, AnalysisRunFailureCodes.FromStoredReason("claude_unreadable_answer"));
        Assert.True(AnalysisRunFailureCodes.All.IsSubsetOf(AnalysisRunFailureCodes.BudgetRefusalCodes));
    }
}
