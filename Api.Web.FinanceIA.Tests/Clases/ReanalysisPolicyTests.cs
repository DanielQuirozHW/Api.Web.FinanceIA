// Project : Api.Web.FinanceIA.Tests
// Created : 2026-07-17

using Api.Web.FinanceIA.Clases;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

public sealed class ReanalysisPolicyTests
{
    [Theory]
    // Completed document: re-analysis hinges on the most recent analysis status.
    [InlineData("Completed", "Rejected",      ReanalysisDecision.AllowAfterRejected)] // Rejected → allowed
    [InlineData("Completed", "Approved",      ReanalysisDecision.DenyNotRejected)]    // Approved → 409
    [InlineData("Completed", "PendingReview", ReanalysisDecision.DenyNotRejected)]
    [InlineData("Completed", "Reviewed",      ReanalysisDecision.DenyNotRejected)]
    [InlineData("Completed", null,            ReanalysisDecision.DenyNoAnalysis)]     // data inconsistency
    // Processing document is always locked, regardless of any analysis status.
    [InlineData("Processing", null,           ReanalysisDecision.DenyProcessing)]     // Processing → 409
    [InlineData("Processing", "Rejected",     ReanalysisDecision.DenyProcessing)]
    // Non-terminal states may proceed.
    [InlineData("Pending", null,              ReanalysisDecision.Allow)]
    [InlineData("Failed",  null,              ReanalysisDecision.Allow)]
    public void Evaluate_ReturnsExpectedDecision(
        string processingStatus, string? latestAnalysisStatus, ReanalysisDecision expected)
    {
        var result = ReanalysisPolicy.Evaluate(processingStatus, latestAnalysisStatus);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Evaluate_StatusMatchIsCaseSensitive()
    {
        // Guards against a status typo/casing bug silently unlocking re-analysis.
        Assert.Equal(ReanalysisDecision.DenyNotRejected,
            ReanalysisPolicy.Evaluate("Completed", "rejected"));
        Assert.Equal(ReanalysisDecision.Allow,
            ReanalysisPolicy.Evaluate("completed", "Rejected"));
    }

    // ── Extraction budget ───────────────────────────────────────────────────────

    [Theory]
    // Budget spent ⇒ refused, whatever non-terminal state the document is in. This is the
    // refusal that keeps an unreadable balance from buying another billable Claude extraction.
    [InlineData("Pending", 2, 2)]
    [InlineData("Failed",  2, 2)]
    [InlineData("Failed",  5, 2)]   // over budget (a hash sibling contributed) still refuses
    [InlineData("Failed",  1, 1)]
    public void Evaluate_WhenExtractionBudgetSpent_Denies(
        string processingStatus, int attempts, int max)
    {
        Assert.Equal(
            ReanalysisDecision.DenyExtractionBudgetExhausted,
            ReanalysisPolicy.Evaluate(processingStatus, null, attempts, max));
    }

    [Theory]
    [InlineData("Pending", 0, 2)]
    [InlineData("Failed",  1, 2)]   // one blip absorbed: the analyst keeps their retry
    public void Evaluate_WhenExtractionBudgetRemains_Allows(
        string processingStatus, int attempts, int max)
    {
        Assert.Equal(
            ReanalysisDecision.Allow,
            ReanalysisPolicy.Evaluate(processingStatus, null, attempts, max));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Evaluate_WhenBudgetIsNotPositive_TheCheckIsSkipped(int max)
    {
        // The documented escape hatch: Billing:MaxExtractionAttemptsPerDocument <= 0 disables the
        // guard entirely, however many attempts have been spent.
        Assert.Equal(
            ReanalysisDecision.Allow,
            ReanalysisPolicy.Evaluate("Failed", null, extractionAttempts: 99, maxExtractionAttempts: max));
    }

    [Fact]
    public void Evaluate_ProcessingOutranksTheExtractionBudget()
    {
        // A run genuinely in flight is a different and more urgent lock, and reporting "budget
        // spent" while one is still going would simply be wrong.
        Assert.Equal(
            ReanalysisDecision.DenyProcessing,
            ReanalysisPolicy.Evaluate("Processing", null, extractionAttempts: 9, maxExtractionAttempts: 2));
    }

    [Theory]
    [InlineData("Rejected",      ReanalysisDecision.AllowAfterRejected)]
    [InlineData("Approved",      ReanalysisDecision.DenyNotRejected)]
    [InlineData(null,            ReanalysisDecision.DenyNoAnalysis)]
    public void Evaluate_CompletedDocumentIgnoresTheExtractionBudget(
        string? latestAnalysisStatus, ReanalysisDecision expected)
    {
        // A completed document is proof that we DID read this balance, so refusing its
        // re-analysis with "we could not read it" would be both wrong and confusing. The budget
        // bounds spend on a file we keep failing to read; this one we did not.
        Assert.Equal(
            expected,
            ReanalysisPolicy.Evaluate("Completed", latestAnalysisStatus, extractionAttempts: 9, maxExtractionAttempts: 2));
    }

    [Fact]
    public void Evaluate_SingleArgumentOverload_IsUnlimited()
    {
        // The overload every existing caller uses must keep its original meaning: no budget to
        // enforce, so a Failed document stays re-analysable.
        Assert.Equal(ReanalysisDecision.Allow, ReanalysisPolicy.Evaluate("Failed", null));
        Assert.Equal(ReanalysisDecision.Allow, ReanalysisPolicy.Evaluate("Pending", null));
    }

    // ── §5.8: the latest analysis read none of the seven base figures ────────────────────────

    [Theory]
    // status,      latest,          readNothing, attempts, max, expected
    [InlineData("Completed",  "PendingReview", true,  0, 2, ReanalysisDecision.AllowAfterUnreadable)]
    [InlineData("Completed",  "Approved",      true,  1, 2, ReanalysisDecision.AllowAfterUnreadable)]
    [InlineData("Completed",  "PendingReview", true,  2, 2, ReanalysisDecision.DenyExtractionBudgetExhausted)]
    [InlineData("Completed",  "PendingReview", true,  9, 0, ReanalysisDecision.AllowAfterUnreadable)]   // budget off
    [InlineData("Completed",  "PendingReview", false, 0, 2, ReanalysisDecision.DenyNotRejected)]
    [InlineData("Completed",  "Rejected",      true,  9, 2, ReanalysisDecision.AllowAfterRejected)]     // Rejected keeps its exemption
    [InlineData("Completed",  "Rejected",      false, 9, 2, ReanalysisDecision.AllowAfterRejected)]
    [InlineData("Completed",  null,            true,  0, 2, ReanalysisDecision.DenyNoAnalysis)]
    [InlineData("Processing", "PendingReview", true,  0, 2, ReanalysisDecision.DenyProcessing)]
    [InlineData("Failed",     null,            true,  0, 2, ReanalysisDecision.Allow)]
    [InlineData("Failed",     null,            true,  2, 2, ReanalysisDecision.DenyExtractionBudgetExhausted)]
    [InlineData("Pending",    null,            false, 0, 2, ReanalysisDecision.Allow)]
    public void Evaluate_WithTheBaseFigureFlag_CoversEveryDecisionPath(
        string status, string? latest, bool readNothing, int attempts, int max, ReanalysisDecision expected)
    {
        Assert.Equal(expected, ReanalysisPolicy.Evaluate(status, latest, readNothing, attempts, max));
    }

    [Theory]
    [InlineData("Completed", "PendingReview", 0, 2)]
    [InlineData("Completed", "Rejected",      5, 2)]
    [InlineData("Failed",    null,            2, 2)]
    [InlineData("Pending",   null,            0, 0)]
    public void TheEarlierOverloads_AreTheNewOneWithTheFlagOff(string status, string? latest, int attempts, int max)
    {
        Assert.Equal(
            ReanalysisPolicy.Evaluate(status, latest, latestAnalysisReadNoBaseFigures: false, attempts, max),
            ReanalysisPolicy.Evaluate(status, latest, attempts, max));
    }
}
