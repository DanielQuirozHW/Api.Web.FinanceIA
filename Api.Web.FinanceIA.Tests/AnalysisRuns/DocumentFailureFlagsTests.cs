// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Xunit;

namespace Api.Web.FinanceIA.Tests.AnalysisRuns;

/// <summary>
/// <see cref="DocumentFailureFlags"/>: the three flags a company document carries, from /runs' own helpers.
/// </summary>
public sealed class DocumentFailureFlagsTests
{
    private static readonly DateTime FailedAt = new(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc);

    private static Document Doc(
        string    status,
        string?   lastError    = null,
        DateTime? lastFailedAt = null,
        string    hash         = "abc",
        Guid?     legajoId     = null,
        string?   legajoStatus = null) => new()
    {
        DocumentId             = Guid.NewGuid(),
        TenantId               = "t",
        EntityId               = Guid.NewGuid(),
        FileHashSHA256         = hash,
        ProcessingStatus       = status,
        ProcessedAt            = status is "Failed" or "Completed" ? FailedAt : null,
        LastExtractionError    = lastError,
        LastExtractionFailedAt = lastFailedAt,
        PersonLegajoId         = legajoId,
        LegajoStatus           = legajoStatus
    };

    private static DocumentResponse Apply(Document document, IReadOnlyDictionary<string, int>? sums, int max = 2)
    {
        var response = new DocumentResponse();
        DocumentFailureFlags.Apply(response, document, sums, max);
        return response;
    }

    [Theory]
    [InlineData("claude_no_financial_statements", "no_financial_statements", "document")]
    [InlineData("claude_refusal",                 "unreadable_document",     "document")]
    [InlineData("claude_api_error",               "file_rejected",           "document")]
    [InlineData("claude_timeout",                 "timeout",                 "ours")]
    [InlineData("claude_server_error",            "service_unavailable",     "ours")]
    [InlineData("claude_max_tokens",              "reading_failed",          "ours")]
    [InlineData("TaskCanceledException",          "interrupted",             "ours")]
    [InlineData("NullReferenceException",         "other",                   "ours")]
    public void Failed_MapsTheDocumentsOwnCode(string stored, string reason, string group)
    {
        var r = Apply(Doc("Failed", stored, FailedAt.AddMilliseconds(5)), new Dictionary<string, int> { ["abc"] = 1 });

        Assert.Equal(reason, r.FailureReason);
        Assert.Equal(group, r.CauseGroup);
        Assert.Equal(1, r.AttemptsRemaining);
    }

    [Fact]
    public void Failed_CodeFromAnEarlierFailure_ReadsOther()
    {
        var r = Apply(Doc("Failed", "claude_no_financial_statements", FailedAt.AddMinutes(-10)), null);

        Assert.Equal(AnalysisRunFailureReasons.Other, r.FailureReason);
        Assert.Equal(AnalysisRunFailureReasons.Ours, r.CauseGroup);
    }

    [Fact]
    public void Failed_NoCodeAtAll_ReadsOther()
        => Assert.Equal(AnalysisRunFailureReasons.Other, Apply(Doc("Failed"), null).FailureReason);

    [Theory]
    [InlineData("Pending")]
    [InlineData("Processing")]
    public void NotFailed_NoReason_ButAttemptsLeft(string status)
    {
        var r = Apply(Doc(status), new Dictionary<string, int> { ["abc"] = 5 });

        Assert.Null(r.FailureReason);
        Assert.Null(r.CauseGroup);
        Assert.Equal(0, r.AttemptsRemaining);
    }

    [Fact]
    public void Completed_AttemptsNull()
        => Assert.Null(Apply(Doc("Completed"), new Dictionary<string, int> { ["abc"] = 0 }).AttemptsRemaining);

    [Fact]
    public void BudgetOff_AttemptsNull()
        => Assert.Null(Apply(Doc("Pending"), new Dictionary<string, int> { ["abc"] = 0 }, max: 0).AttemptsRemaining);

    [Fact]
    public void SumsUnknown_AttemptsNull()
        => Assert.Null(Apply(Doc("Pending"), null).AttemptsRemaining);

    [Fact]
    public void BlankHash_AttemptsNull()
        => Assert.Null(Apply(Doc("Pending", hash: " "), new Dictionary<string, int>()).AttemptsRemaining);

    [Fact]
    public void HashWithNoSpend_HasTheFullBudget()
        => Assert.Equal(2, Apply(Doc("Pending"), new Dictionary<string, int>()).AttemptsRemaining);

    [Fact]
    public void LegajoDocument_AllThreeNull()
    {
        var r = Apply(
            Doc("Failed", "claude_timeout", FailedAt, legajoId: Guid.NewGuid(), legajoStatus: LegajoStatuses.Failed),
            new Dictionary<string, int> { ["abc"] = 0 });

        Assert.Null(r.FailureReason);
        Assert.Null(r.CauseGroup);
        Assert.Null(r.AttemptsRemaining);
    }
}
