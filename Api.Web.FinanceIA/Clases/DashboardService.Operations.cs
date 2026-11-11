// Project : Api.Web.FinanceIA
// Created : 2026-10-23

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

// GET /api/dashboard/operations. What ApiCallLogs can and cannot say about the integrations, and how old
// the source snapshots behind the live portfolio are. Plan usage and pending invitations are NOT here —
// GET /api/billing/usage and GET /api/bff/settings/invitations?status=pending already serve them.
//
// An OPTIONAL integration (LUFE, CASFOG, NOSIS) appears in integrations.services and freshness.sources
// only when the tenant can actually use it — entitled by its profile AND available in this installation,
// per IIntegrationAccessService, the gate the enrichment endpoints apply. Otherwise "never queried" would
// equal every PyME for a source the tenant cannot use, and that source would read as the worst forever.
// ARCA, BCRA and Claude are base integrations and are always listed. A hidden source is simply absent
// from the arrays — the response contract does not change.
public sealed partial class DashboardService
{
    private const int IntegrationsWindowHours = 24;
    private const int FreshDays               = 30;
    private const int StaleDays               = 90;

    /// <summary>
    /// The ApiCallLogs.Service values the code writes today, each from its writer:
    /// ARCA (EntitiesController / FiadoresController after each constancia or padrón call), LUFE
    /// (LufeService), NOSIS (NosisService), Claude (AnalysisPipeline, AnalysisScoringStage,
    /// LegajoMessagesClient) and ClaudeChat (AnalysisController.ChatAsync). Listed even when idle — unless
    /// optional and not usable by the tenant (<see cref="IsListed"/>).
    /// </summary>
    internal static readonly IReadOnlyList<string> TrackedIntegrations = ["ARCA", "LUFE", "NOSIS", "Claude", "ClaudeChat"];

    /// <summary>
    /// Vendors whose calls write NO ApiCallLogs row — BcraService and CasfogService take no
    /// IApiCallLogService. Listed with tracked = false so their absence reads as "not measured", never as
    /// "no calls".
    /// </summary>
    internal static readonly IReadOnlyList<string> UntrackedIntegrations = [RescoreSource.Bcra, RescoreSource.Casfog];

    /// <summary>The integrations <see cref="IsListed"/> may hide, in the order the summary log names them.</summary>
    private static readonly IReadOnlyList<string> OptionalSources = [RescoreSource.Lufe, RescoreSource.Casfog, RescoreSource.Nosis];

    /// <inheritdoc />
    public async Task<OperationsResponse> GetOperationsAsync(string tenantId, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var degraded = new List<string>();

        // ONE resolution for both sections, so they cannot disagree about a source. It fails open (a
        // degraded or unreadable profile lists everything the installation can reach) and throws only
        // on cancellation, which is not a section failure and propagates.
        var access = await _integrationAccess.ResolveUsableAsync(tenantId, ct).ConfigureAwait(false);
        var hidden = OptionalSources.Where(source => !IsListed(source, access)).ToList();

        var integrations = await RunSectionAsync(DashboardSections.Integrations, tenantId, null, degraded,
            () => BuildIntegrationsAsync(tenantId, nowUtc, access, ct), ct).ConfigureAwait(false);

        var freshness = await RunSectionAsync(DashboardSections.Freshness, tenantId, null, degraded,
            () => BuildFreshnessAsync(tenantId, nowUtc, access, ct), ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Dashboard operations for tenant {TenantId}: services={Services} calls={Calls} failures={Failures} noData={NoData} freshnessEntities={FreshnessEntities} degradedSections={DegradedCount} hiddenSources={HiddenSources}",
            tenantId, integrations.Value?.Services.Count, integrations.Value?.Services.Sum(s => s.Calls ?? 0),
            integrations.Value?.Services.Sum(s => s.Failures ?? 0), integrations.Value?.Services.Sum(s => s.NoData ?? 0),
            freshness.Value?.EntityCount, degraded.Count, string.Join(",", hidden));

        return new OperationsResponse
        {
            GeneratedAt      = nowUtc,
            DegradedSections = degraded,
            Integrations     = integrations.Value,
            Freshness        = freshness.Value
        };
    }

    private async Task<OperationsIntegrations> BuildIntegrationsAsync(
        string tenantId, DateTime nowUtc, OptionalIntegrationAccess access, CancellationToken ct)
    {
        var fromUtc = nowUtc.AddHours(-IntegrationsWindowHours);

        var aggregates = AggregatePerService(await _dashboardRepository
            .GetApiCallOutcomesAsync(tenantId, fromUtc, nowUtc, ct)
            .ConfigureAwait(false));

        var durations = (await _dashboardRepository
                .GetApiCallDurationsAsync(tenantId, fromUtc, nowUtc, ct)
                .ConfigureAwait(false))
            .GroupBy(d => d.Service, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(d => d.DurationMs).Order().ToList(), StringComparer.Ordinal);

        IntegrationServiceItem Measured(string service)
        {
            var aggregate = aggregates.GetValueOrDefault(service);
            return new IntegrationServiceItem
            {
                Service       = service,
                Tracked       = true,
                Calls         = aggregate?.Calls ?? 0,
                Failures      = aggregate?.Failures ?? 0,
                NoData        = aggregate?.NoData ?? 0,
                LastSuccessAt = aggregate?.LastSuccessAt,
                LastFailureAt = aggregate?.LastFailureAt,
                P95DurationMs = NearestRank(durations.GetValueOrDefault(service), 0.95)
            };
        }

        var services = new List<IntegrationServiceItem>();
        services.AddRange(TrackedIntegrations
            .Where(service => IsListed(service, access))
            .Select(Measured));

        // A vendor listed as untracked that DOES show rows has started writing them: report what is there.
        services.AddRange(UntrackedIntegrations
            .Where(service => IsListed(service, access))
            .Select(service => aggregates.ContainsKey(service)
                ? Measured(service)
                : new IntegrationServiceItem
                {
                    Service       = service,
                    Tracked       = false,
                    Calls         = null,
                    Failures      = null,
                    NoData        = null,
                    LastSuccessAt = null,
                    LastFailureAt = null,
                    P95DurationMs = null
                }));

        // Any other value in the window — a legacy writer, or one added after this list. Every optional
        // integration is in one of the two lists above, so rows a hidden one wrote before it was switched
        // off never bring it back through here.
        services.AddRange(aggregates.Keys
            .Where(service => !TrackedIntegrations.Contains(service, StringComparer.Ordinal)
                           && !UntrackedIntegrations.Contains(service, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(Measured));

        return new OperationsIntegrations
        {
            WindowStartUtc = fromUtc,
            WindowEndUtc   = nowUtc,
            Services       = services
        };
    }

    private async Task<OperationsFreshness> BuildFreshnessAsync(
        string tenantId, DateTime nowUtc, OptionalIntegrationAccess access, CancellationToken ct)
    {
        var (entityCount, sources) = await _dashboardRepository
            .GetFreshnessAsync(tenantId, nowUtc.AddDays(-FreshDays), nowUtc.AddDays(-StaleDays), ct)
            .ConfigureAwait(false);

        return new OperationsFreshness
        {
            EntityCount = entityCount,
            Sources     = sources
                .Where(s => IsListed(s.Source, access))
                .Select(s => new FreshnessSourceItem
                {
                    Source      = s.Source,
                    Under30Days = s.Under30Days,
                    Days30To90  = s.Days30To90,
                    Over90Days  = s.Over90Days,
                    Never       = s.Never
                })
                .ToList()
        };
    }

    /// <summary>
    /// Folds the outcome rows into one aggregate per service. A vendor's "no data" answer
    /// (<see cref="ApiCallNoDataClassifier"/>) is an answered call: counted in Calls and NoData, never in
    /// Failures, and it moves LastSuccessAt — the vendor was reachable, authenticated and answering, which
    /// is what "último OK" asserts. Only real failures reach Failures and LastFailureAt.
    /// </summary>
    internal static Dictionary<string, ApiCallServiceAggregate> AggregatePerService(
        IEnumerable<DashboardApiCallOutcomeRow> outcomes)
        => outcomes
            .GroupBy(o => o.Service, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g =>
            {
                int calls = 0, failures = 0, noData = 0;
                DateTime? lastSuccessAt = null, lastFailureAt = null;

                foreach (var o in g)
                {
                    calls += o.Calls;

                    if (o.IsSuccess || ApiCallNoDataClassifier.IsNoData(
                            o.Service, o.IsSuccess, o.ResponseStatus, o.HasErrorMessage, o.HasLufeNoDataMarker))
                    {
                        if (!o.IsSuccess)
                            noData += o.Calls;
                        lastSuccessAt = Later(lastSuccessAt, o.LastCalledAt);
                    }
                    else
                    {
                        failures += o.Calls;
                        lastFailureAt = Later(lastFailureAt, o.LastCalledAt);
                    }
                }

                return new ApiCallServiceAggregate(calls, failures, noData, lastSuccessAt, lastFailureAt);
            }, StringComparer.Ordinal);

    private static DateTime? Later(DateTime? current, DateTime candidate)
        => current is null || candidate > current ? candidate : current;

    /// <summary>One service's figures over the window, after <see cref="AggregatePerService"/>.</summary>
    internal sealed record ApiCallServiceAggregate(
        int Calls, int Failures, int NoData, DateTime? LastSuccessAt, DateTime? LastFailureAt);

    /// <summary>
    /// False only for an OPTIONAL integration (LUFE, CASFOG, NOSIS) the tenant cannot use — not entitled by
    /// its profile, or not available in this installation. Every other name — ARCA, BCRA, Claude, ClaudeChat,
    /// a legacy ApiCallLogs value — is always listed. Ordinal, like the ApiCallLogs.Service and freshness
    /// source values it is compared to.
    /// </summary>
    internal static bool IsListed(string service, OptionalIntegrationAccess access) => service switch
    {
        RescoreSource.Lufe   => access.Lufe,
        RescoreSource.Casfog => access.Casfog,
        RescoreSource.Nosis  => access.Nosis,
        _                    => true
    };

    /// <summary>
    /// The <paramref name="p"/> percentile of <paramref name="sortedAscending"/> by nearest rank — a value
    /// that was actually observed. Null for a missing or empty list.
    /// </summary>
    internal static int? NearestRank(IReadOnlyList<int>? sortedAscending, double p)
    {
        if (sortedAscending is null || sortedAscending.Count == 0)
            return null;

        var index = (int)Math.Ceiling(p * sortedAscending.Count) - 1;
        return sortedAscending[Math.Clamp(index, 0, sortedAscending.Count - 1)];
    }
}
