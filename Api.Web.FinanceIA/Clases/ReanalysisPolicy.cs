// Project : Api.Web.FinanceIA
// Created : 2026-07-17

using System;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Outcome of evaluating whether a document may be (re)analysed.
/// </summary>
public enum ReanalysisDecision
{
    /// <summary>The document has never been completed (Pending/Failed/…); analysis may proceed.</summary>
    Allow,

    /// <summary>The document is Completed and its most recent analysis was Rejected; re-analysis is permitted.</summary>
    AllowAfterRejected,

    /// <summary>An analysis is currently in flight (Processing); locked to prevent concurrent runs.</summary>
    DenyProcessing,

    /// <summary>The document is Completed and its most recent analysis was not Rejected; re-analysis is blocked.</summary>
    DenyNotRejected,

    /// <summary>The document is Completed but has no analysis on record (data inconsistency); blocked to be safe.</summary>
    DenyNoAnalysis,

    /// <summary>
    /// This balance file has already spent its extraction budget on failed attempts; another
    /// trigger would buy another Claude call for a document we have twice failed to read.
    /// </summary>
    DenyExtractionBudgetExhausted,

    /// <summary>
    /// The document is Completed, its most recent analysis was NOT Rejected, but that analysis's stored
    /// balance read none of the seven base figures (<see cref="BalanceBaseFigures"/>) — an analysis from
    /// before the no-financial-statements gate. Re-analysis is permitted without rejecting it first; it stays
    /// untouched as history. The file's extraction budget still applies.
    /// </summary>
    AllowAfterUnreadable
}

/// <summary>
/// Pure decision logic for whether a document may be (re)analysed via
/// <c>POST /api/analyses</c>. A document is analysable while Pending or Failed;
/// a Processing document is always locked (no concurrent analyses); and a
/// Completed document may only be re-analysed when its most recent analysis was
/// Rejected — preserving that prior Rejected analysis as history — or when that analysis read none of
/// the seven base figures (<see cref="ReanalysisDecision.AllowAfterUnreadable"/>), which keeps it as
/// history too, and is subject to the extraction budget like a Pending or Failed document.
///
/// <para>
/// A Pending or Failed document is analysable only while its balance FILE still has extraction
/// budget left. A failed extraction never consumes one of the tenant's monthly análisis (that row
/// is written after Claude answers), so without this the retry button could buy billable
/// extractions on an unreadable PDF indefinitely. The one-argument overload keeps the original
/// behaviour — unlimited — for callers with no budget to enforce.
/// </para>
/// </summary>
public static class ReanalysisPolicy
{
    /// <summary>
    /// Decides whether analysis may proceed for a document.
    /// </summary>
    /// <param name="processingStatus">The document's <c>ProcessingStatus</c>.</param>
    /// <param name="latestAnalysisStatus">
    /// The <c>Status</c> of the most recent analysis for the document, or null if none exists.
    /// Only consulted when <paramref name="processingStatus"/> is <c>"Completed"</c>.
    /// </param>
    public static ReanalysisDecision Evaluate(string? processingStatus, string? latestAnalysisStatus)
        => Evaluate(processingStatus, latestAnalysisStatus, extractionAttempts: 0, maxExtractionAttempts: 0);

    /// <summary>
    /// Decides whether analysis may proceed for a document, also refusing one whose balance file
    /// has already spent its extraction budget on failed attempts.
    /// </summary>
    /// <param name="processingStatus">The document's <c>ProcessingStatus</c>.</param>
    /// <param name="latestAnalysisStatus">
    /// The <c>Status</c> of the most recent analysis for the document, or null if none exists.
    /// Only consulted when <paramref name="processingStatus"/> is <c>"Completed"</c>.
    /// </param>
    /// <param name="extractionAttempts">
    /// Failed extraction attempts already spent on this FILE — summed across every non-deleted
    /// document of the tenant sharing its <c>FileHashSHA256</c>, so a re-upload of the identical
    /// bytes inherits the budget instead of resetting it.
    /// </param>
    /// <param name="maxExtractionAttempts">
    /// The budget, from <c>Billing:MaxExtractionAttemptsPerDocument</c>. Zero or negative means
    /// unlimited: the check is skipped entirely.
    /// </param>
    public static ReanalysisDecision Evaluate(
        string? processingStatus,
        string? latestAnalysisStatus,
        int     extractionAttempts,
        int     maxExtractionAttempts)
        => Evaluate(processingStatus, latestAnalysisStatus, latestAnalysisReadNoBaseFigures: false,
            extractionAttempts, maxExtractionAttempts);

    /// <summary>
    /// Decides whether analysis may proceed for a document, also letting a Completed document whose latest
    /// (non-Rejected) analysis read none of the seven base figures be analysed again.
    /// </summary>
    /// <param name="processingStatus">The document's <c>ProcessingStatus</c>.</param>
    /// <param name="latestAnalysisStatus">
    /// The <c>Status</c> of the most recent analysis for the document, or null if none exists.
    /// Only consulted when <paramref name="processingStatus"/> is <c>"Completed"</c>.
    /// </param>
    /// <param name="latestAnalysisReadNoBaseFigures">
    /// True when that analysis's stored balance holds none of the seven base figures
    /// (<see cref="BalanceBaseFigures.ReadNone"/>). Only consulted for a Completed document whose latest
    /// analysis is not Rejected; false keeps the original rule.
    /// </param>
    /// <param name="extractionAttempts">Failed extraction attempts already spent on this FILE.</param>
    /// <param name="maxExtractionAttempts">The budget; zero or negative means unlimited.</param>
    public static ReanalysisDecision Evaluate(
        string? processingStatus,
        string? latestAnalysisStatus,
        bool    latestAnalysisReadNoBaseFigures,
        int     extractionAttempts,
        int     maxExtractionAttempts)
    {
        if (string.Equals(processingStatus, "Processing", StringComparison.Ordinal))
            return ReanalysisDecision.DenyProcessing;

        if (string.Equals(processingStatus, "Completed", StringComparison.Ordinal))
        {
            if (latestAnalysisStatus is null)
                return ReanalysisDecision.DenyNoAnalysis;

            if (string.Equals(latestAnalysisStatus, "Rejected", StringComparison.Ordinal))
                return ReanalysisDecision.AllowAfterRejected;

            if (!latestAnalysisReadNoBaseFigures)
                return ReanalysisDecision.DenyNotRejected;

            // That analysis read nothing, so this is a new attempt at reading the file — the budget the
            // Pending/Failed path applies holds here too (the Rejected arm above keeps its exemption: a
            // balance that WAS read).
            return BudgetSpent(extractionAttempts, maxExtractionAttempts)
                ? ReanalysisDecision.DenyExtractionBudgetExhausted
                : ReanalysisDecision.AllowAfterUnreadable;
        }

        // Ordered LAST, and deliberately below the Completed arm. A completed document is proof
        // that we did read this balance, so refusing its re-analysis with "we could not read it"
        // would be both wrong and confusing — the budget bounds spend on a file we keep failing
        // to read, and this one we did not.
        //
        // It sits below DenyProcessing for a related reason: a run genuinely in flight is a
        // different and more urgent lock, and reporting "budget spent" while one is still going
        // would simply be inaccurate.
        if (BudgetSpent(extractionAttempts, maxExtractionAttempts))
            return ReanalysisDecision.DenyExtractionBudgetExhausted;

        // Pending, Failed, or any other non-terminal state: analysis may proceed.
        return ReanalysisDecision.Allow;
    }

    private static bool BudgetSpent(int extractionAttempts, int maxExtractionAttempts)
        => maxExtractionAttempts > 0 && extractionAttempts >= maxExtractionAttempts;
}
