// Project : Api.Web.FinanceIA
// Created : 2026-10-23

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Read-only data access behind the role dashboards: my-work, approvals, portfolio, oversight, operations.
/// </summary>
/// <remarks>
/// Every method is scoped by tenant on EVERY table it touches — <see cref="FinanciaDbContext"/> has no
/// global query filter, and AnalysisId, ResultId and UserId are globally unique keys that would
/// otherwise reach another tenant's rows. Every read is AsNoTracking, one query per list, projected to
/// <see cref="DashboardAnalysisRow"/> so no nvarchar(max) blob leaves the database. An analysis with no
/// AnalysisResults row is invisible here, on purpose: the Portal links by ResultId.
///
/// <para>
/// Its own interface rather than new members on IAnalysisRepository / IAnalysisResultRepository: those
/// have dozens of hand-rolled test stubs, and a read model for one screen is not their business.
/// </para>
/// </remarks>
public interface IDashboardRepository
{
    /// <summary>
    /// The user's PendingReview analyses with no decision on record (RequestedBy = user, ReviewedBy
    /// null): risk level descending (Critical, High, Medium, Low, anything else), then GeneratedAt
    /// ascending, at most <paramref name="take"/>; plus the size of the whole set. Rows carry
    /// <see cref="DashboardAnalysisRow.HasBalance"/>.
    /// </summary>
    Task<(IReadOnlyList<DashboardAnalysisRow> Items, int TotalCount)> GetMyPendingAsync(
        string tenantId, Guid userId, int take, CancellationToken ct);

    /// <summary>
    /// The same set as <see cref="GetMyPendingAsync"/> restricted to EnrichmentStatus = 'Degraded',
    /// uncapped, with <see cref="DashboardAnalysisRow.HasBalance"/>.
    /// </summary>
    Task<IReadOnlyList<DashboardAnalysisRow>> GetMyPendingDegradedAsync(
        string tenantId, Guid userId, CancellationToken ct);

    /// <summary>
    /// PendingReview analyses with ReviewedBy set — returned for rework — that the user requested OR
    /// pre-approved: newest ReviewedAt first, at most <paramref name="take"/>; plus the total. Rows carry
    /// <see cref="DashboardAnalysisRow.ReviewNotes"/>.
    /// </summary>
    Task<(IReadOnlyList<DashboardAnalysisRow> Items, int TotalCount)> GetReturnedAsync(
        string tenantId, Guid userId, int take, CancellationToken ct);

    /// <summary>
    /// PreApproved analyses the user pre-approved: oldest PreApprovedAt first, at most
    /// <paramref name="take"/>; plus the total.
    /// </summary>
    Task<(IReadOnlyList<DashboardAnalysisRow> Items, int TotalCount)> GetAwaitingMySignatureAsync(
        string tenantId, Guid userId, int take, CancellationToken ct);

    /// <summary>The user's latest <paramref name="take"/> analyses in any status, newest CreatedAt first.</summary>
    Task<IReadOnlyList<DashboardAnalysisRow>> GetMyRecentAsync(
        string tenantId, Guid userId, int take, CancellationToken ct);

    /// <summary>Every PreApproved analysis of the tenant, oldest PreApprovedAt first. Uncapped.</summary>
    Task<IReadOnlyList<DashboardAnalysisRow>> GetPreApprovedAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// The analyses among <paramref name="analysisIds"/> on which the user holds an
    /// <see cref="AnalysisVoteValues.Approved"/> vote.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetAnalysisIdsApprovedByAsync(
        string tenantId, Guid userId, IReadOnlyCollection<Guid> analysisIds, CancellationToken ct);

    /// <summary>
    /// The FiltersJson of each analysis' solicitante — the earliest-created participant with role
    /// Solicitante, the row the detail reads. Analyses with none are absent.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetSolicitanteFiltersAsync(
        string tenantId, IReadOnlyCollection<Guid> analysisIds, CancellationToken ct);

    /// <summary>
    /// Documents.ProcessingStatus by DocumentId. No IsDeleted filter — the detail's
    /// IDocumentRepository.GetByIdAsync applies none either, and canRescore must agree with it.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetDocumentStatusesAsync(
        string tenantId, IReadOnlyCollection<Guid> documentIds, CancellationToken ct);

    /// <summary>The user's vote rows cast at or after <paramref name="sinceUtc"/>.</summary>
    Task<IReadOnlyList<DashboardVoteRow>> GetVotesByUserSinceAsync(
        string tenantId, Guid userId, DateTime sinceUtc, CancellationToken ct);

    /// <summary>Analyses whose ReviewedBy is the user and ReviewedAt is at or after <paramref name="sinceUtc"/>.</summary>
    Task<IReadOnlyList<DashboardAnalysisRow>> GetReviewedByUserSinceAsync(
        string tenantId, Guid userId, DateTime sinceUtc, CancellationToken ct);

    /// <summary>The rows of the given analyses, in no particular order.</summary>
    Task<IReadOnlyList<DashboardAnalysisRow>> GetByAnalysisIdsAsync(
        string tenantId, IReadOnlyCollection<Guid> analysisIds, CancellationToken ct);

    // ── portfolio ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Active and Suspended cupos grouped by (Currency, Status): how many, how many distinct entities,
    /// their Amount.
    /// </summary>
    Task<IReadOnlyList<DashboardLineGroupRow>> GetLineTotalsAsync(string tenantId, CancellationToken ct);

    /// <summary>The cupo-occupying operations of the Active cupos grouped by the cupo's currency: count and Amount.</summary>
    Task<IReadOnlyList<DashboardLineGroupRow>> GetOccupiedByCurrencyAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Active cupos grouped by (Currency, AnalysisKind, AiSuggestedLevel) of the cupo's analysis —
    /// LastRaiseAnalysisId, else OriginAnalysisId — with null keys where the analysis or its result
    /// does not resolve in the tenant.
    /// </summary>
    Task<IReadOnlyList<DashboardLineGroupRow>> GetLinesByRiskAsync(string tenantId, CancellationToken ct);

    /// <summary>The cupo-occupying operations of the Active cupos grouped as <see cref="GetLinesByRiskAsync"/>.</summary>
    Task<IReadOnlyList<DashboardLineGroupRow>> GetOccupiedByRiskAsync(string tenantId, CancellationToken ct);

    /// <summary>The distinct currencies of the tenant's Active cupos, ordered.</summary>
    Task<IReadOnlyList<string>> GetActiveLineCurrenciesAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// The Active cupos of <paramref name="currency"/> with the most utilizado, highest first then by
    /// EntityId, at most <paramref name="take"/>.
    /// </summary>
    Task<IReadOnlyList<DashboardExposureRow>> GetTopExposuresAsync(
        string tenantId, string currency, int take, CancellationToken ct);

    /// <summary>
    /// How many Active cupos with Amount &gt; 0 have utilizado ≥ <paramref name="thresholdPct"/>% of it,
    /// grouped by currency: count and Amount. Only currencies with at least one such cupo come back.
    /// </summary>
    Task<IReadOnlyList<DashboardLineGroupRow>> GetHighUtilizationCountsAsync(
        string tenantId, decimal thresholdPct, CancellationToken ct);

    /// <summary>
    /// The Active cupos of <paramref name="currency"/> counted by <see cref="GetHighUtilizationCountsAsync"/>,
    /// utilization descending, then Amount descending, then EntityId, at most <paramref name="take"/>.
    /// </summary>
    Task<IReadOnlyList<DashboardExposureRow>> GetHighUtilizationAsync(
        string tenantId, string currency, decimal thresholdPct, int take, CancellationToken ct);

    /// <summary>Active cupos grouped by (Currency, Entities.Sector as stored).</summary>
    Task<IReadOnlyList<DashboardLineGroupRow>> GetLinesBySectorAsync(string tenantId, CancellationToken ct);

    /// <summary>The cupo-occupying operations of the Active cupos grouped by (Currency, Entities.Sector as stored).</summary>
    Task<IReadOnlyList<DashboardLineGroupRow>> GetOccupiedBySectorAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Stored-'Vigente' operations of Active cupos with ValidUntil before <paramref name="validUntilBefore"/>,
    /// grouped by (Currency, ValidUntil date).
    /// </summary>
    Task<IReadOnlyList<DashboardMaturityGroupRow>> GetMaturityGroupsAsync(
        string tenantId, DateTime validUntilBefore, CancellationToken ct);

    /// <summary>
    /// Stored-'Vigente' operations of Active cupos with ValidUntil at or after <paramref name="validFrom"/>,
    /// soonest first, at most <paramref name="take"/>.
    /// </summary>
    Task<IReadOnlyList<DashboardUpcomingOperationRow>> GetUpcomingMaturitiesAsync(
        string tenantId, DateTime validFrom, int take, CancellationToken ct);

    /// <summary>
    /// Every Active cupo with the BCRA estado and maximum situación — read with JSON_VALUE, never the
    /// blob — of its entity now and of its analysis' result.
    /// </summary>
    Task<IReadOnlyList<DashboardBcraComparisonRow>> GetBcraComparisonsAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Every Active cupo with its utilizado and the BCRA estado, maximum situación and last-query instant of
    /// its entity's CURRENT snapshot — read with JSON_VALUE, never the blob. One statement.
    /// </summary>
    Task<IReadOnlyList<DashboardBcraCurrentRow>> GetBcraCurrentAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// The PyMEs with at least one Active cupo and the latest approved analysis of each — the PYME_REVIEW_DUE
    /// input, read by the same query (<see cref="PymeReviewQueries"/>) <see cref="ICreditLineRepository"/>
    /// serves it to the cupo alerts with. One row per PyME.
    /// </summary>
    Task<IReadOnlyList<PymeReviewSource>> GetLatestApprovedAnalysesOfActiveEntitiesAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Every operation of an Active cupo that occupies cupo (every stored status but Cancelada), with the
    /// CUPO's currency and entity. Its effective status is the caller's to decide, with CreditLineRules.
    /// One statement.
    /// </summary>
    Task<IReadOnlyList<DashboardUpcomingOperationRow>> GetOccupyingOperationsOfActiveLinesAsync(string tenantId, CancellationToken ct);

    // ── oversight ─────────────────────────────────────────────────────────────────

    /// <summary>Every PendingReview analysis of the tenant: oldest GeneratedAt first, at most <paramref name="take"/>; plus the total.</summary>
    Task<(IReadOnlyList<DashboardAnalysisRow> Items, int TotalCount)> GetTeamQueueAsync(
        string tenantId, int take, CancellationToken ct);

    /// <summary>The whole PendingReview queue grouped by requester.</summary>
    Task<IReadOnlyList<DashboardRequesterQueueRow>> GetTeamQueueByRequesterAsync(string tenantId, CancellationToken ct);

    /// <summary>How many analyses of the tenant are PreApproved.</summary>
    Task<int> CountPreApprovedAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Approved analyses with ReviewedAt at or after <paramref name="sinceUtc"/> that are a balance analysis
    /// at High or Critical, or a persona humana analysis. Newest ReviewedAt first. Uncapped: the persona
    /// suggestion is derived in C#.
    /// </summary>
    Task<IReadOnlyList<DashboardAnalysisRow>> GetAgainstSuggestionCandidatesAsync(
        string tenantId, DateTime sinceUtc, CancellationToken ct);

    /// <summary>
    /// The latest decisions — Approved, Rejected, or PendingReview with a reviewer — newest ReviewedAt
    /// first, at most <paramref name="take"/>. Rows carry <see cref="DashboardAnalysisRow.ReviewNotes"/>.
    /// </summary>
    Task<IReadOnlyList<DashboardAnalysisRow>> GetRecentDecisionsAsync(string tenantId, int take, CancellationToken ct);

    /// <summary>Analyses.ReviewNotes of the given analyses.</summary>
    Task<IReadOnlyDictionary<Guid, string?>> GetReviewNotesAsync(
        string tenantId, IReadOnlyCollection<Guid> analysisIds, CancellationToken ct);

    /// <summary>
    /// The columns the score-reliability verdict needs for the given results (RescoredAt, the analysis'
    /// FiscalYear, ScoreBreakdownJson), keyed by ResultId, tenant-scoped on both tables. Page-sized: called
    /// only with the rows a section shows, after its cap.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, DashboardScoreReliabilitySource>> GetScoreReliabilitySourcesAsync(
        string tenantId, IReadOnlyCollection<Guid> resultIds, CancellationToken ct);

    /// <summary>
    /// The latest <paramref name="take"/> re-scores, newest first, without their JSON columns, with their
    /// analysis' EntityId and AnalysisKind — null when that analysis is not this tenant's.
    /// </summary>
    Task<IReadOnlyList<DashboardRescoreRow>> GetRecentRescoresAsync(string tenantId, int take, CancellationToken ct);

    /// <summary>Analyses created in [<paramref name="startUtc"/>, <paramref name="endUtc"/>) grouped by status now.</summary>
    Task<IReadOnlyList<DashboardStatusCountRow>> GetCreatedByStatusAsync(
        string tenantId, DateTime startUtc, DateTime endUtc, CancellationToken ct);

    /// <summary>CreatedAt and ReviewedAt of the analyses created in the window that are now Approved or Rejected.</summary>
    Task<IReadOnlyList<DashboardDecisionTimeRow>> GetDecisionTimesAsync(
        string tenantId, DateTime startUtc, DateTime endUtc, CancellationToken ct);

    // ── operations ────────────────────────────────────────────────────────────────

    /// <summary>
    /// ApiCallLogs in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>] aggregated per Service and
    /// outcome (IsSuccess, ResponseStatus, whether ErrorMessage is set).
    /// </summary>
    Task<IReadOnlyList<DashboardApiCallOutcomeRow>> GetApiCallOutcomesAsync(
        string tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken ct);

    /// <summary>Service and DurationMs of the window's rows that carry a duration.</summary>
    Task<IReadOnlyList<DashboardApiCallDurationRow>> GetApiCallDurationsAsync(
        string tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken ct);

    /// <summary>
    /// How many entities hold an Active cupo, and per source how many of them were refreshed after
    /// <paramref name="cutoff30Utc"/>, between the cutoffs (inclusive), before <paramref name="cutoff90Utc"/>,
    /// and never.
    /// </summary>
    Task<(int EntityCount, IReadOnlyList<DashboardFreshnessRow> Sources)> GetFreshnessAsync(
        string tenantId, DateTime cutoff30Utc, DateTime cutoff90Utc, CancellationToken ct);
}
