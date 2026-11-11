// Project : Api.Web.FinanceIA
// Created : 2026-10-23

using Api.Web.FinanceIA.Clases.Time;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;

namespace Api.Web.FinanceIA.Clases;

// GET /api/dashboard/oversight. The tenant-wide twin of my-work and approvals: the same projection, the
// same act derivation (ActOf), the same persona suggestion (PersonEvaluationService.Suggest over the
// solicitante's filters), and months that are ARGENTINE months (ArgentineTime).
public sealed partial class DashboardService
{
    private const int TeamQueueCap          = 50;
    private const int AgainstSuggestionCap  = 20;
    private const int AgainstSuggestionDays = 90;
    private const int RecentDecisionsCap    = 20;
    private const int RecentRescoresCap     = 10;

    /// <inheritdoc />
    public async Task<OversightResponse> GetOversightAsync(string tenantId, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var degraded = new List<string>();

        var teamQueue = await RunSectionAsync(DashboardSections.TeamQueue, tenantId, null, degraded,
            () => BuildTeamQueueAsync(tenantId, nowUtc, ct), ct).ConfigureAwait(false);

        var against = await RunSectionAsync(DashboardSections.AgainstSuggestion, tenantId, null, degraded,
            () => BuildAgainstSuggestionAsync(tenantId, nowUtc, ct), ct).ConfigureAwait(false);

        var decisions = await RunSectionAsync(DashboardSections.RecentDecisions, tenantId, null, degraded,
            () => BuildRecentDecisionsAsync(tenantId, ct), ct).ConfigureAwait(false);

        var rescores = await RunSectionAsync(DashboardSections.RecentRescores, tenantId, null, degraded,
            () => BuildRecentRescoresAsync(tenantId, ct), ct).ConfigureAwait(false);

        var monthStats = await RunSectionAsync(DashboardSections.MonthStats, tenantId, null, degraded,
            () => BuildMonthStatsAsync(tenantId, nowUtc, ct), ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Dashboard oversight for tenant {TenantId}: teamQueue={TeamQueue} preApproved={PreApproved} againstSuggestion={AgainstSuggestion} recentDecisions={RecentDecisions} recentRescores={RecentRescores} degradedSections={DegradedCount}",
            tenantId, teamQueue.Value?.TotalCount, teamQueue.Value?.PreApprovedCount, against.Value?.TotalCount,
            decisions.Value?.Count, rescores.Value?.Count, degraded.Count);

        return new OversightResponse
        {
            GeneratedAt       = nowUtc,
            DegradedSections  = degraded,
            TeamQueue         = teamQueue.Value,
            AgainstSuggestion = against.Value,
            RecentDecisions   = decisions.Value,
            RecentRescores    = rescores.Value,
            MonthStats        = monthStats.Value
        };
    }

    private async Task<OversightTeamQueue> BuildTeamQueueAsync(string tenantId, DateTime nowUtc, CancellationToken ct)
    {
        var (rows, total) = await _dashboardRepository
            .GetTeamQueueAsync(tenantId, TeamQueueCap, ct)
            .ConfigureAwait(false);

        var byRequester = await _dashboardRepository
            .GetTeamQueueByRequesterAsync(tenantId, ct)
            .ConfigureAwait(false);

        var preApproved = await _dashboardRepository
            .CountPreApprovedAsync(tenantId, ct)
            .ConfigureAwait(false);

        var entities = await EntityNamesAsync(tenantId, rows, ct).ConfigureAwait(false);
        var users    = await UsersAsync(tenantId,
            rows.Select(r => (Guid?)r.RequestedBy).Concat(byRequester.Select(r => (Guid?)r.UserId)), ct).ConfigureAwait(false);
        var reliability = await ScoreReliabilityAsync(tenantId, rows, "oversight.team-queue", ct).ConfigureAwait(false);

        return new OversightTeamQueue
        {
            TotalCount = total,
            Items      = rows
                .Select(r =>
                {
                    entities.TryGetValue(r.EntityId, out var entity);
                    return new TeamQueueItem
                    {
                        ResultId         = r.ResultId,
                        AnalysisId       = r.AnalysisId,
                        AnalysisKind     = r.AnalysisKind,
                        EntityId         = r.EntityId,
                        EntityName       = entity?.BusinessName,
                        EntityCuit       = entity?.CUIT,
                        RequestedBy      = r.RequestedBy,
                        RequestedByName  = ActorName(r.RequestedBy, users),
                        Returned         = r.ReviewedBy is not null,
                        AiSuggestedLevel = r.AiSuggestedLevel,
                        AiSuggestedScore = r.AiSuggestedScore,
                        RequestedAmount  = r.RequestedAmount,
                        GeneratedAt      = r.GeneratedAt,
                        AgeDays          = AgeDays(r.GeneratedAt, nowUtc),
                        ScoreReliability = reliability.GetValueOrDefault(r.ResultId)
                    };
                })
                .ToList(),
            ByRequester = byRequester
                .OrderByDescending(r => r.Pending + r.Returned)
                .ThenBy(r => r.OldestGeneratedAt)
                .ThenBy(r => r.UserId)
                .Select(r => new TeamQueueRequester
                {
                    UserId        = r.UserId,
                    Name          = ActorName(r.UserId, users),
                    Pending       = r.Pending,
                    Returned      = r.Returned,
                    OldestAgeDays = AgeDays(r.OldestGeneratedAt, nowUtc)
                })
                .ToList(),
            PreApprovedCount = preApproved
        };
    }

    private async Task<DashboardPage<AgainstSuggestionItem>> BuildAgainstSuggestionAsync(
        string tenantId, DateTime nowUtc, CancellationToken ct)
    {
        // The WHOLE candidate set, not a page of it: the persona suggestion is derived below, and capping
        // before it would make totalCount wrong.
        var candidates = await _dashboardRepository
            .GetAgainstSuggestionCandidatesAsync(tenantId, nowUtc.AddDays(-AgainstSuggestionDays), ct)
            .ConfigureAwait(false);

        var personaIds = candidates
            .Where(r => string.Equals(r.AnalysisKind, AnalysisKinds.PersonaHumana, StringComparison.Ordinal))
            .Select(r => r.AnalysisId)
            .ToList();

        IReadOnlyDictionary<Guid, string> solicitanteFilters = new Dictionary<Guid, string>();
        if (personaIds.Count > 0)
        {
            solicitanteFilters = await _dashboardRepository
                .GetSolicitanteFiltersAsync(tenantId, personaIds, ct)
                .ConfigureAwait(false);
        }

        var against = new List<(DashboardAnalysisRow Row, string Reason, string? Suggestion)>();
        foreach (var row in candidates)
        {
            if (string.Equals(row.AnalysisKind, AnalysisKinds.PersonaHumana, StringComparison.Ordinal))
            {
                // As approvals' waitingForMe derives it — and as the detail shows it: from the solicitante's
                // frozen filters, null without a solicitante.
                var filtersJson = solicitanteFilters.GetValueOrDefault(row.AnalysisId);
                var suggestion  = filtersJson is null
                    ? null
                    : PersonEvaluationService.Suggest(PersonEvaluationJson.FiltersOf(filtersJson));

                if (string.Equals(suggestion, PersonSuggestions.RechazoSugerido, StringComparison.Ordinal))
                    against.Add((row, DashboardAgainstSuggestionReasons.PersonSuggestion, suggestion));
            }
            else if (row.AiSuggestedLevel is DashboardRiskBuckets.High or DashboardRiskBuckets.Critical)
            {
                against.Add((row, DashboardAgainstSuggestionReasons.AiLevel, null));
            }
        }

        var page     = against.Take(AgainstSuggestionCap).ToList();
        var pageRows = page.Select(e => e.Row).ToList();

        var notes    = await _dashboardRepository
            .GetReviewNotesAsync(tenantId, pageRows.Select(r => r.AnalysisId).ToList(), ct)
            .ConfigureAwait(false);
        var entities = await EntityNamesAsync(tenantId, pageRows, ct).ConfigureAwait(false);
        var users    = await UsersAsync(tenantId, pageRows.Select(r => r.ReviewedBy), ct).ConfigureAwait(false);

        return new DashboardPage<AgainstSuggestionItem>
        {
            TotalCount = against.Count,
            Items      = page
                .Select(e =>
                {
                    var r = e.Row;
                    entities.TryGetValue(r.EntityId, out var entity);
                    return new AgainstSuggestionItem
                    {
                        ResultId         = r.ResultId,
                        AnalysisId       = r.AnalysisId,
                        AnalysisKind     = r.AnalysisKind,
                        EntityId         = r.EntityId,
                        EntityName       = entity?.BusinessName,
                        EntityCuit       = entity?.CUIT,
                        Reason           = e.Reason,
                        AiSuggestedLevel = r.AiSuggestedLevel,
                        PersonSuggestion = e.Suggestion,
                        DecidedBy        = r.ReviewedBy,
                        DecidedByName    = ActorName(r.ReviewedBy, users),
                        ReviewedAt       = r.ReviewedAt,
                        ReviewNotes      = notes.GetValueOrDefault(r.AnalysisId),
                        DecisionAmount   = r.DecisionAmount,
                        DecisionCurrency = r.DecisionCurrency
                    };
                })
                .ToList()
        };
    }

    private async Task<IReadOnlyList<RecentDecisionItem>> BuildRecentDecisionsAsync(string tenantId, CancellationToken ct)
    {
        var rows = await _dashboardRepository
            .GetRecentDecisionsAsync(tenantId, RecentDecisionsCap, ct)
            .ConfigureAwait(false);

        var entities = await EntityNamesAsync(tenantId, rows, ct).ConfigureAwait(false);
        var users    = await UsersAsync(tenantId, rows.Select(r => r.ReviewedBy), ct).ConfigureAwait(false);

        // The read already keeps only the statuses whose act the row proves, so ActOf names every row.
        return rows
            .Select(r => (Row: r, Act: ActOf(r)))
            .Where(x => x.Act is not null && x.Row.ReviewedAt.HasValue)
            .Select(x =>
            {
                var r = x.Row;
                entities.TryGetValue(r.EntityId, out var entity);
                return new RecentDecisionItem
                {
                    ResultId         = r.ResultId,
                    AnalysisId       = r.AnalysisId,
                    AnalysisKind     = r.AnalysisKind,
                    EntityId         = r.EntityId,
                    EntityName       = entity?.BusinessName,
                    EntityCuit       = entity?.CUIT,
                    Act              = x.Act!,
                    DecidedBy        = r.ReviewedBy,
                    DecidedByName    = ActorName(r.ReviewedBy, users),
                    ReviewedAt       = r.ReviewedAt!.Value,
                    ReviewNotes      = r.ReviewNotes,
                    DecisionAmount   = r.DecisionAmount,
                    DecisionCurrency = r.DecisionCurrency,
                    Status           = r.Status
                };
            })
            .ToList();
    }

    private async Task<IReadOnlyList<RecentRescoreItem>> BuildRecentRescoresAsync(string tenantId, CancellationToken ct)
    {
        var rows = await _dashboardRepository
            .GetRecentRescoresAsync(tenantId, RecentRescoresCap, ct)
            .ConfigureAwait(false);

        var entities = await EntityNamesAsync(tenantId,
            rows.Where(r => r.EntityId.HasValue).Select(r => r.EntityId!.Value), ct).ConfigureAwait(false);
        var users    = await UsersAsync(tenantId, rows.Select(r => (Guid?)r.RescoredBy), ct).ConfigureAwait(false);

        return rows
            .Select(r =>
            {
                EntityNameProjection? entity = null;
                if (r.EntityId.HasValue)
                    entities.TryGetValue(r.EntityId.Value, out entity);

                return new RecentRescoreItem
                {
                    RescoreId                = r.RescoreId,
                    ResultId                 = r.ResultId,
                    AnalysisId               = r.AnalysisId,
                    AnalysisKind             = r.AnalysisKind,
                    EntityId                 = r.EntityId,
                    EntityName               = entity?.BusinessName,
                    EntityCuit               = entity?.CUIT,
                    RescoredAt               = r.RescoredAt,
                    RescoredBy               = r.RescoredBy,
                    RescoredByName           = ActorName(r.RescoredBy, users),
                    PreviousScore            = r.PreviousScore,
                    NewScore                 = r.NewScore,
                    PreviousLevel            = r.PreviousLevel,
                    NewLevel                 = r.NewLevel,
                    PreviousEnrichmentStatus = r.PreviousEnrichmentStatus,
                    NewEnrichmentStatus      = r.NewEnrichmentStatus,
                    // Persona only: a balance row has no suggestions node, and stays exactly as it was.
                    PreviousSuggestion       = IsPersona(r) ? r.PreviousSuggestion : null,
                    NewSuggestion            = IsPersona(r) ? r.NewSuggestion : null
                };
            })
            .ToList();
    }

    private static bool IsPersona(DashboardRescoreRow row)
        => string.Equals(row.AnalysisKind, AnalysisKinds.PersonaHumana, StringComparison.Ordinal);

    private async Task<IReadOnlyList<MonthStatsItem>> BuildMonthStatsAsync(string tenantId, DateTime nowUtc, CancellationToken ct)
    {
        // Argentine months, as half-open UTC windows: an analysis created at 01:00 UTC on the 1st is still
        // in the PREVIOUS month in Buenos Aires.
        var current  = ArgentineTime.CurrentPeriod(nowUtc);
        var previous = current.Month == 1
            ? ArgentineTime.PeriodFor(current.Year - 1, 12)
            : ArgentineTime.PeriodFor(current.Year, current.Month - 1);

        var stats = new List<MonthStatsItem>();
        foreach (var period in new[] { current, previous })
        {
            var byStatus = await _dashboardRepository
                .GetCreatedByStatusAsync(tenantId, period.StartUtc, period.EndUtc, ct)
                .ConfigureAwait(false);

            var times = await _dashboardRepository
                .GetDecisionTimesAsync(tenantId, period.StartUtc, period.EndUtc, ct)
                .ConfigureAwait(false);

            var hours = times
                .Select(t => (t.ReviewedAt - t.CreatedAt).TotalHours)
                .Order()
                .ToList();

            stats.Add(new MonthStatsItem
            {
                Year                  = period.Year,
                Month                 = period.Month,
                PeriodStartUtc        = period.StartUtc,
                PeriodEndUtc          = period.EndUtc,
                Created               = byStatus.Sum(s => s.Count),
                ByStatus              = byStatus
                    .OrderByDescending(s => s.Count)
                    .ThenBy(s => s.Status, StringComparer.Ordinal)
                    .Select(s => new MonthStatusCount { Status = s.Status, Count = s.Count })
                    .ToList(),
                Decided               = hours.Count,
                MedianHoursToDecision = Percentile(hours, 0.50),
                P90HoursToDecision    = Percentile(hours, 0.90)
            });
        }

        return stats;
    }

    /// <summary>
    /// The <paramref name="p"/> percentile of <paramref name="sortedAscending"/> by linear interpolation
    /// between the closest ranks — SQL Server's PERCENTILE_CONT — rounded to 2 decimals. Null for an empty
    /// list; the single value for a list of one.
    /// </summary>
    internal static double? Percentile(IReadOnlyList<double> sortedAscending, double p)
    {
        if (sortedAscending.Count == 0)
            return null;

        var rank  = p * (sortedAscending.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        var value = sortedAscending[lower] + (sortedAscending[upper] - sortedAscending[lower]) * (rank - lower);

        return Math.Round(value, 2, MidpointRounding.AwayFromZero);
    }
}
