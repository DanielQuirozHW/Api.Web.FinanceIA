// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-23

using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>The real repository, except that the named methods throw. Shared by the dashboard service suites.</summary>
internal sealed class FailingDashboardRepository(IDashboardRepository inner, params string[] failing) : IDashboardRepository
{
    private readonly HashSet<string> _failing = [.. failing];

    public Func<Exception> Throw { get; init; } = () => new InvalidOperationException("simulated section failure");

    private Task<T> Run<T>(string method, Func<Task<T>> call)
        => _failing.Contains(method) ? Task.FromException<T>(Throw()) : call();

    public Task<(IReadOnlyList<DashboardAnalysisRow> Items, int TotalCount)> GetMyPendingAsync(string tenantId, Guid userId, int take, CancellationToken ct)
        => Run(nameof(GetMyPendingAsync), () => inner.GetMyPendingAsync(tenantId, userId, take, ct));
    public Task<IReadOnlyList<DashboardAnalysisRow>> GetMyPendingDegradedAsync(string tenantId, Guid userId, CancellationToken ct)
        => Run(nameof(GetMyPendingDegradedAsync), () => inner.GetMyPendingDegradedAsync(tenantId, userId, ct));
    public Task<(IReadOnlyList<DashboardAnalysisRow> Items, int TotalCount)> GetReturnedAsync(string tenantId, Guid userId, int take, CancellationToken ct)
        => Run(nameof(GetReturnedAsync), () => inner.GetReturnedAsync(tenantId, userId, take, ct));
    public Task<(IReadOnlyList<DashboardAnalysisRow> Items, int TotalCount)> GetAwaitingMySignatureAsync(string tenantId, Guid userId, int take, CancellationToken ct)
        => Run(nameof(GetAwaitingMySignatureAsync), () => inner.GetAwaitingMySignatureAsync(tenantId, userId, take, ct));
    public Task<IReadOnlyList<DashboardAnalysisRow>> GetMyRecentAsync(string tenantId, Guid userId, int take, CancellationToken ct)
        => Run(nameof(GetMyRecentAsync), () => inner.GetMyRecentAsync(tenantId, userId, take, ct));
    public Task<IReadOnlyList<DashboardAnalysisRow>> GetPreApprovedAsync(string tenantId, CancellationToken ct)
        => Run(nameof(GetPreApprovedAsync), () => inner.GetPreApprovedAsync(tenantId, ct));
    public Task<IReadOnlySet<Guid>> GetAnalysisIdsApprovedByAsync(string tenantId, Guid userId, IReadOnlyCollection<Guid> analysisIds, CancellationToken ct)
        => Run(nameof(GetAnalysisIdsApprovedByAsync), () => inner.GetAnalysisIdsApprovedByAsync(tenantId, userId, analysisIds, ct));
    public Task<IReadOnlyDictionary<Guid, string>> GetSolicitanteFiltersAsync(string tenantId, IReadOnlyCollection<Guid> analysisIds, CancellationToken ct)
        => Run(nameof(GetSolicitanteFiltersAsync), () => inner.GetSolicitanteFiltersAsync(tenantId, analysisIds, ct));
    public Task<IReadOnlyDictionary<Guid, string>> GetDocumentStatusesAsync(string tenantId, IReadOnlyCollection<Guid> documentIds, CancellationToken ct)
        => Run(nameof(GetDocumentStatusesAsync), () => inner.GetDocumentStatusesAsync(tenantId, documentIds, ct));
    public Task<IReadOnlyList<DashboardVoteRow>> GetVotesByUserSinceAsync(string tenantId, Guid userId, DateTime sinceUtc, CancellationToken ct)
        => Run(nameof(GetVotesByUserSinceAsync), () => inner.GetVotesByUserSinceAsync(tenantId, userId, sinceUtc, ct));
    public Task<IReadOnlyList<DashboardAnalysisRow>> GetReviewedByUserSinceAsync(string tenantId, Guid userId, DateTime sinceUtc, CancellationToken ct)
        => Run(nameof(GetReviewedByUserSinceAsync), () => inner.GetReviewedByUserSinceAsync(tenantId, userId, sinceUtc, ct));
    public Task<IReadOnlyList<DashboardAnalysisRow>> GetByAnalysisIdsAsync(string tenantId, IReadOnlyCollection<Guid> analysisIds, CancellationToken ct)
        => Run(nameof(GetByAnalysisIdsAsync), () => inner.GetByAnalysisIdsAsync(tenantId, analysisIds, ct));
    public Task<IReadOnlyList<DashboardLineGroupRow>> GetLineTotalsAsync(string tenantId, CancellationToken ct)
        => Run(nameof(GetLineTotalsAsync), () => inner.GetLineTotalsAsync(tenantId, ct));
    public Task<IReadOnlyList<DashboardLineGroupRow>> GetOccupiedByCurrencyAsync(string tenantId, CancellationToken ct)
        => Run(nameof(GetOccupiedByCurrencyAsync), () => inner.GetOccupiedByCurrencyAsync(tenantId, ct));
    public Task<IReadOnlyList<DashboardLineGroupRow>> GetLinesByRiskAsync(string tenantId, CancellationToken ct)
        => Run(nameof(GetLinesByRiskAsync), () => inner.GetLinesByRiskAsync(tenantId, ct));
    public Task<IReadOnlyList<DashboardLineGroupRow>> GetOccupiedByRiskAsync(string tenantId, CancellationToken ct)
        => Run(nameof(GetOccupiedByRiskAsync), () => inner.GetOccupiedByRiskAsync(tenantId, ct));
    public Task<IReadOnlyList<string>> GetActiveLineCurrenciesAsync(string tenantId, CancellationToken ct)
        => Run(nameof(GetActiveLineCurrenciesAsync), () => inner.GetActiveLineCurrenciesAsync(tenantId, ct));
    public Task<IReadOnlyList<DashboardExposureRow>> GetTopExposuresAsync(string tenantId, string currency, int take, CancellationToken ct)
        => Run(nameof(GetTopExposuresAsync), () => inner.GetTopExposuresAsync(tenantId, currency, take, ct));
    public Task<IReadOnlyList<DashboardLineGroupRow>> GetHighUtilizationCountsAsync(string tenantId, decimal thresholdPct, CancellationToken ct)
        => Run(nameof(GetHighUtilizationCountsAsync), () => inner.GetHighUtilizationCountsAsync(tenantId, thresholdPct, ct));
    public Task<IReadOnlyList<DashboardExposureRow>> GetHighUtilizationAsync(string tenantId, string currency, decimal thresholdPct, int take, CancellationToken ct)
        => Run(nameof(GetHighUtilizationAsync), () => inner.GetHighUtilizationAsync(tenantId, currency, thresholdPct, take, ct));
    public Task<IReadOnlyList<DashboardLineGroupRow>> GetLinesBySectorAsync(string tenantId, CancellationToken ct)
        => Run(nameof(GetLinesBySectorAsync), () => inner.GetLinesBySectorAsync(tenantId, ct));
    public Task<IReadOnlyList<DashboardLineGroupRow>> GetOccupiedBySectorAsync(string tenantId, CancellationToken ct)
        => Run(nameof(GetOccupiedBySectorAsync), () => inner.GetOccupiedBySectorAsync(tenantId, ct));
    public Task<IReadOnlyList<DashboardMaturityGroupRow>> GetMaturityGroupsAsync(string tenantId, DateTime validUntilBefore, CancellationToken ct)
        => Run(nameof(GetMaturityGroupsAsync), () => inner.GetMaturityGroupsAsync(tenantId, validUntilBefore, ct));
    public Task<IReadOnlyList<DashboardUpcomingOperationRow>> GetUpcomingMaturitiesAsync(string tenantId, DateTime validFrom, int take, CancellationToken ct)
        => Run(nameof(GetUpcomingMaturitiesAsync), () => inner.GetUpcomingMaturitiesAsync(tenantId, validFrom, take, ct));
    public Task<IReadOnlyList<DashboardBcraComparisonRow>> GetBcraComparisonsAsync(string tenantId, CancellationToken ct)
        => Run(nameof(GetBcraComparisonsAsync), () => inner.GetBcraComparisonsAsync(tenantId, ct));
    public Task<IReadOnlyList<DashboardBcraCurrentRow>> GetBcraCurrentAsync(string tenantId, CancellationToken ct)
        => Run(nameof(GetBcraCurrentAsync), () => inner.GetBcraCurrentAsync(tenantId, ct));
    public Task<IReadOnlyList<PymeReviewSource>> GetLatestApprovedAnalysesOfActiveEntitiesAsync(string tenantId, CancellationToken ct)
        => Run(nameof(GetLatestApprovedAnalysesOfActiveEntitiesAsync), () => inner.GetLatestApprovedAnalysesOfActiveEntitiesAsync(tenantId, ct));
    public Task<IReadOnlyList<DashboardUpcomingOperationRow>> GetOccupyingOperationsOfActiveLinesAsync(string tenantId, CancellationToken ct)
        => Run(nameof(GetOccupyingOperationsOfActiveLinesAsync), () => inner.GetOccupyingOperationsOfActiveLinesAsync(tenantId, ct));
    public Task<(IReadOnlyList<DashboardAnalysisRow> Items, int TotalCount)> GetTeamQueueAsync(string tenantId, int take, CancellationToken ct)
        => Run(nameof(GetTeamQueueAsync), () => inner.GetTeamQueueAsync(tenantId, take, ct));
    public Task<IReadOnlyList<DashboardRequesterQueueRow>> GetTeamQueueByRequesterAsync(string tenantId, CancellationToken ct)
        => Run(nameof(GetTeamQueueByRequesterAsync), () => inner.GetTeamQueueByRequesterAsync(tenantId, ct));
    public Task<int> CountPreApprovedAsync(string tenantId, CancellationToken ct)
        => Run(nameof(CountPreApprovedAsync), () => inner.CountPreApprovedAsync(tenantId, ct));
    public Task<IReadOnlyList<DashboardAnalysisRow>> GetAgainstSuggestionCandidatesAsync(string tenantId, DateTime sinceUtc, CancellationToken ct)
        => Run(nameof(GetAgainstSuggestionCandidatesAsync), () => inner.GetAgainstSuggestionCandidatesAsync(tenantId, sinceUtc, ct));
    public Task<IReadOnlyList<DashboardAnalysisRow>> GetRecentDecisionsAsync(string tenantId, int take, CancellationToken ct)
        => Run(nameof(GetRecentDecisionsAsync), () => inner.GetRecentDecisionsAsync(tenantId, take, ct));
    public Task<IReadOnlyDictionary<Guid, string?>> GetReviewNotesAsync(string tenantId, IReadOnlyCollection<Guid> analysisIds, CancellationToken ct)
        => Run(nameof(GetReviewNotesAsync), () => inner.GetReviewNotesAsync(tenantId, analysisIds, ct));
    public Task<IReadOnlyDictionary<Guid, DashboardScoreReliabilitySource>> GetScoreReliabilitySourcesAsync(string tenantId, IReadOnlyCollection<Guid> resultIds, CancellationToken ct)
        => Run(nameof(GetScoreReliabilitySourcesAsync), () => inner.GetScoreReliabilitySourcesAsync(tenantId, resultIds, ct));
    public Task<IReadOnlyList<DashboardRescoreRow>> GetRecentRescoresAsync(string tenantId, int take, CancellationToken ct)
        => Run(nameof(GetRecentRescoresAsync), () => inner.GetRecentRescoresAsync(tenantId, take, ct));
    public Task<IReadOnlyList<DashboardStatusCountRow>> GetCreatedByStatusAsync(string tenantId, DateTime startUtc, DateTime endUtc, CancellationToken ct)
        => Run(nameof(GetCreatedByStatusAsync), () => inner.GetCreatedByStatusAsync(tenantId, startUtc, endUtc, ct));
    public Task<IReadOnlyList<DashboardDecisionTimeRow>> GetDecisionTimesAsync(string tenantId, DateTime startUtc, DateTime endUtc, CancellationToken ct)
        => Run(nameof(GetDecisionTimesAsync), () => inner.GetDecisionTimesAsync(tenantId, startUtc, endUtc, ct));
    public Task<IReadOnlyList<DashboardApiCallOutcomeRow>> GetApiCallOutcomesAsync(string tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
        => Run(nameof(GetApiCallOutcomesAsync), () => inner.GetApiCallOutcomesAsync(tenantId, fromUtc, toUtc, ct));
    public Task<IReadOnlyList<DashboardApiCallDurationRow>> GetApiCallDurationsAsync(string tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
        => Run(nameof(GetApiCallDurationsAsync), () => inner.GetApiCallDurationsAsync(tenantId, fromUtc, toUtc, ct));
    public Task<(int EntityCount, IReadOnlyList<DashboardFreshnessRow> Sources)> GetFreshnessAsync(string tenantId, DateTime cutoff30Utc, DateTime cutoff90Utc, CancellationToken ct)
        => Run(nameof(GetFreshnessAsync), () => inner.GetFreshnessAsync(tenantId, cutoff30Utc, cutoff90Utc, ct));
}
