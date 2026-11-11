// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-07-13

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Persistence operations for <see cref="Analysis"/> records in [dbo].[Analyses].
/// </summary>
public interface IAnalysisRepository
{
    /// <summary>
    /// Persists a new analysis record and returns the saved entity.
    /// </summary>
    Task<Analysis> CreateAsync(Analysis analysis, CancellationToken ct);

    /// <summary>
    /// Returns the analysis with the given id, or null if not found.
    /// </summary>
    Task<Analysis?> GetByIdAsync(Guid analysisId, CancellationToken ct);

    /// <summary>
    /// Returns all analyses matching the given set of ids.
    /// </summary>
    Task<IReadOnlyList<Analysis>> GetByIdsAsync(IEnumerable<Guid> analysisIds, CancellationToken ct);

    /// <summary>
    /// Returns the most recent analysis (by <see cref="Analysis.CreatedAt"/>) for the given
    /// document within the tenant, or null if the document has no analyses.
    /// </summary>
    Task<Analysis?> GetLatestByDocumentIdAsync(Guid documentId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Persists changes to an existing analysis record and returns the saved entity.
    /// </summary>
    Task<Analysis> UpdateAsync(Analysis analysis, CancellationToken ct);

    /// <summary>
    /// Counts the analyses a tenant created inside one billing period, for quota metering.
    ///
    /// <para>
    /// Counts EVERY row whatever its Status. A Rejected analysis still counts, and so does the
    /// re-analysis that follows it — both were work Claude was paid for.
    /// </para>
    ///
    /// <para>
    /// ⚠ WHAT THIS CAN AND CANNOT SEE. The [dbo].[Analyses] row is written on the SUCCESS path
    /// only (AnalysisPipeline.RunAsync, after extraction and scoring), because AiModel and
    /// PromptVersion are NOT NULL and unknown until Claude answers. A trigger that FAILED —
    /// storage fault, Claude 502 — leaves no row and therefore consumes no quota. The billed
    /// unit is a completed analysis, not an attempt.
    /// </para>
    ///
    /// <para>
    /// The period is read off <c>CreatedAt</c>. A synchronous run stamps it with the moment
    /// extraction started; a run queued with <c>Prefer: respond-async</c> stamps it with the moment
    /// the request passed the quota gate (<c>AnalysisRun.AcceptedAtUtc</c>), so a run admitted on the
    /// last evening of a month is billed to that month even if it finishes in the next.
    /// </para>
    ///
    /// <para>
    /// That gap is intentional and is NOT what bounds the spend on a failed attempt. Because a
    /// failure is free here, an unreadable balance could otherwise be retried against the same
    /// slot indefinitely, each retry a real billable extraction. What stops it is a separate
    /// per-FILE extraction budget — <c>Documents.ExtractionAttempts</c> summed by
    /// <c>FileHashSHA256</c>, checked in <c>ReanalysisPolicy</c> before the Claude call. Do not
    /// "fix" it by starting to count failures here.
    /// </para>
    ///
    /// <para>
    /// Month attribution is still correct despite the late insert: the row's CreatedAt is set to
    /// processingStartedAt, the TRIGGER instant, so an analysis started at 23:58 on the 31st
    /// counts in the month it was started even though the row lands minutes later.
    /// </para>
    ///
    /// <para>
    /// The window arrives precomputed rather than being rebuilt here from a year and a month. The
    /// billing period is the ARGENTINE calendar month while CreatedAt is stored in UTC, so the
    /// boundary is a conversion, not a construction — and it must be the SAME boundary the chat
    /// token count uses. <c>ArgentineTime.CurrentPeriod</c> is the one place that computes it; this
    /// signature is what stops a second, subtly different copy appearing here.
    /// </para>
    /// </summary>
    /// <param name="tenantId">Firebase tenant slug to scope the count to.</param>
    /// <param name="periodStartUtc">Start of the window, UTC, INCLUSIVE.</param>
    /// <param name="periodEndUtc">End of the window, UTC, EXCLUSIVE.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<int> CountByTenantInPeriodAsync(
        string tenantId,
        DateTime periodStartUtc,
        DateTime periodEndUtc,
        CancellationToken ct);

    /// <summary>
    /// Returns the latest analysis status (by <see cref="Analysis.CreatedAt"/>) per entity
    /// for the given entity ids, tenant-isolated. Entities with no analysis are absent from
    /// the map. Used to project latestAnalysisStatus onto the entities list without N+1.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetLatestStatusByEntityIdsAsync(
        IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns, per entity, the most recent SCORED analysis result: the stored
    /// <see cref="AnalysisResult.AiSuggestedScore"/> together with the
    /// <see cref="AnalysisResult.AiSuggestedLevel"/> persisted on the SAME row — never a level derived
    /// from the score. "Most recent" is by <see cref="Analysis.CreatedAt"/> (as
    /// <see cref="GetLatestStatusByEntityIdsAsync"/>), then by result generation time; results with no
    /// score (a failed run, a persona humana analysis) are skipped, so a newer unscored analysis
    /// does not hide the last real score. Tenant-isolated on both tables. Entities with no scored
    /// result are absent from the map. Each entry also carries that row's score-reliability inputs
    /// (scalars, plus its ScoreBreakdownJson read in one more page-sized query for the winning rows only),
    /// so the PyMEs list can say whether the score is provisional.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, EntityLatestScoreProjection>> GetLatestScoreByEntityIdsAsync(
        IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct);

    /// <summary>
    /// Writes the final APPROVED record on an analysis, but ONLY while it is still 'PreApproved'.
    /// This is the quorum-crossing write: the one moment a credit decision becomes committed.
    /// </summary>
    /// <remarks>
    /// A single conditional UPDATE guarded on the expected current status, mirroring
    /// <see cref="ICreditLineRepository.SetStatusAsync"/>. It is what makes the crossing happen
    /// EXACTLY ONCE when two managers vote at the same instant: both may observe the tally reaching
    /// quorum, but only one UPDATE matches a row. The loser gets 0 and reports the analysis as
    /// already confirmed rather than overwriting the winner's signature with its own.
    ///
    /// <para>
    /// Deliberately NOT <see cref="UpdateAsync"/>, which saves the whole entity unconditionally and
    /// is last-writer-wins — exactly the property that must not hold here.
    /// </para>
    ///
    /// <para>
    /// <paramref name="reviewNotes"/> keeps the endpoint's "only overwrite when supplied" semantics:
    /// null leaves whatever the column already held rather than clearing it.
    /// </para>
    /// </remarks>
    /// <returns><c>true</c> when this call performed the crossing; <c>false</c> when the analysis was
    /// no longer 'PreApproved' — someone else already settled it.</returns>
    Task<bool> TryConfirmFromPreApprovedAsync(
        Guid              analysisId,
        string            tenantId,
        Guid              reviewedBy,
        DateTime          reviewedAt,
        decimal?          decisionAmount,
        string?           decisionCurrency,
        string?           confirmedOperationsJson,
        string?           reviewNotes,
        CancellationToken ct);

    /// <summary>
    /// The seven base figures of the stored balance of <paramref name="analysisId"/>'s result, scoped to
    /// <paramref name="tenantId"/>, read through <c>JSON_VALUE</c> so the <c>BalanceJson</c> blob is never
    /// materialised; null when the analysis has no result row. Used by POST /api/analyses to let a Completed
    /// document whose latest analysis read none of them be analysed again (<c>ReanalysisPolicy</c>).
    /// </summary>
    Task<BalanceBaseFiguresProjection?> GetBalanceBaseFiguresAsync(Guid analysisId, string tenantId, CancellationToken ct);
}
