// Project : Api.Web.FinanceIA
// Created : 2026-10-02

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Emits the per-user notification that tells an analyst their analysis has finished — or could not
/// be finished — so they can leave the processing screen and come back from the bell.
/// </summary>
/// <remarks>
/// POST /api/analyses takes two to three minutes, and from the Claude call onward the server keeps
/// working even if the browser has gone (see <c>serverOwnsWork</c> in <c>AnalysisPipeline.RunAsync</c>,
/// the long part of <c>AnalysisController.TriggerAsync</c>); with <c>Prefer: respond-async</c> the whole
/// run happens after the 202. This is the hand-back at the end of that work.
///
/// <para>
/// Emitted on EVERY completion, not only when the client disconnected: the server cannot reliably
/// tell whether the browser is still waiting, and the bell doubles as a record of the run.
/// </para>
///
/// <para>
/// STRICTLY SECONDARY. The analysis is the deliverable; the notification is a delivery convenience
/// on top of it. The implementation owns its own try/catch and never throws, so a notification
/// failure can never change the analysis outcome or the HTTP response.
/// </para>
/// </remarks>
public interface IAnalysisCompletionNotifier
{
    /// <summary>
    /// Tells the requester their analysis completed, linking the notification to the result.
    /// </summary>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="userId">The user who triggered the analysis — the addressee.</param>
    /// <param name="analysisId">The new analysis, for log correlation only.</param>
    /// <param name="resultId">
    /// The <c>AnalysisResults.ResultId</c>. Stored as SourceId because the Portal deep-links to
    /// /dashboard/analyses/{sourceId}, which is the ResultId-keyed route.
    /// </param>
    /// <param name="entityName">Business name of the analysed company, for the copy. May be null.</param>
    /// <param name="fiscalYear">Fiscal year of the balance, for the copy. Omitted when not positive.</param>
    /// <param name="score">
    /// The internal motor score (0–100) when scoring produced one; null when it did not. Restated on
    /// the NOSIS 0–1000 scale for the copy.
    /// </param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task NotifyCompletedAsync(
        string            tenantId,
        Guid              userId,
        Guid              analysisId,
        Guid              resultId,
        string?           entityName,
        int               fiscalYear,
        decimal?          score,
        CancellationToken ct);

    /// <summary>
    /// Tells the requester their analysis could not be completed after the server took the work on.
    /// </summary>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="userId">The user who triggered the analysis — the addressee.</param>
    /// <param name="documentId">The document that was being analysed, for log correlation only.</param>
    /// <param name="entityId">The entity the document belongs to; its name is resolved for the copy.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task NotifyFailedAsync(
        string            tenantId,
        Guid              userId,
        Guid              documentId,
        Guid              entityId,
        CancellationToken ct);
}
