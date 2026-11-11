// Project : Api.Web.FinanceIA
// Created : 2026-11-08

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>
/// Fills the three failure flags of a company <see cref="DocumentResponse"/> — <c>failureReason</c>,
/// <c>causeGroup</c>, <c>attemptsRemaining</c> — with GET /api/analyses/runs' own rules, so the new-analysis
/// wizard can tell a file with no balance, or with its budget spent, from a good one. Pure.
///
/// <para>
/// NO SECOND MAPPING. The stored reason is read by <see cref="AnalysisRunListService.ResolveStoredReason"/>
/// (the document's code counts only when written by its LATEST failure), mapped by
/// <see cref="AnalysisRunFailureReasons.FromStoredReason"/> / <see cref="AnalysisRunFailureReasons.CauseGroupOf"/>,
/// and the attempts left come from <see cref="AnalysisRunListService.AttemptsRemainingFor"/> over the batched
/// per-hash sums. The list does not read the <c>Analysis.Failed</c> audit rows /runs also consults, so a
/// failure whose reason lives only there (<c>stale_processing</c>, <c>consent_required</c>) reads
/// <c>other</c> here.
/// </para>
/// </summary>
public static class DocumentFailureFlags
{
    /// <summary>Documents.ProcessingStatus of a failed balance run.</summary>
    public const string FailedStatus = "Failed";

    /// <summary>Documents.ProcessingStatus of a finished balance run.</summary>
    public const string CompletedStatus = "Completed";

    /// <summary>
    /// Whether the document is a company document whose flags are filled — not a persona humana legajo
    /// document (their pipeline state is <c>LegajoStatus</c>, never <c>ProcessingStatus</c>).
    /// </summary>
    public static bool AppliesTo(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.PersonLegajoId is null && document.LegajoStatus is null;
    }

    /// <summary>
    /// Whether the document's attempts left are reported (and so whether its hash belongs in the batched
    /// read): a company document that is not Completed.
    /// </summary>
    public static bool NeedsAttempts(Document document)
        => AppliesTo(document)
           && !string.Equals(document.ProcessingStatus, CompletedStatus, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sets the three flags on <paramref name="response"/> from <paramref name="document"/>.
    /// <paramref name="attemptsByHash"/> is the batched per-hash sum, or null when the budget is off or the
    /// read failed; <paramref name="maxAttempts"/> is Billing:MaxExtractionAttemptsPerDocument.
    /// </summary>
    public static void Apply(
        DocumentResponse                   response,
        Document                           document,
        IReadOnlyDictionary<string, int>?  attemptsByHash,
        int                                maxAttempts)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(document);

        response.FailureReason     = null;
        response.CauseGroup        = null;
        response.AttemptsRemaining = null;

        if (!AppliesTo(document))
            return;

        if (string.Equals(document.ProcessingStatus, FailedStatus, StringComparison.OrdinalIgnoreCase))
        {
            var (storedReason, _) = AnalysisRunListService.ResolveStoredReason(
                new AnalysisRunDocumentProjection
                {
                    DocumentId             = document.DocumentId,
                    EntityId               = document.EntityId,
                    ProcessedAt            = document.ProcessedAt,
                    FileHashSha256         = document.FileHashSHA256,
                    LastExtractionError    = document.LastExtractionError,
                    LastExtractionFailedAt = document.LastExtractionFailedAt
                },
                auditsNewestFirst: null);

            var reason = AnalysisRunFailureReasons.FromStoredReason(storedReason);
            response.FailureReason = reason;
            response.CauseGroup    = AnalysisRunFailureReasons.CauseGroupOf(reason);
        }

        if (NeedsAttempts(document))
        {
            response.AttemptsRemaining = AnalysisRunListService.AttemptsRemainingFor(
                document.FileHashSHA256, attemptsByHash, maxAttempts);
        }
    }
}
