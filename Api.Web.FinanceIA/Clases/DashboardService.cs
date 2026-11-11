// Project : Api.Web.FinanceIA
// Created : 2026-10-23

using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Default implementation of <see cref="IDashboardService"/>. Registered Scoped, like the repositories
/// it reads through.
/// </summary>
/// <remarks>
/// <para>
/// NOTHING HERE RE-IMPLEMENTS A RULE. Each figure comes from the code the detail and the decision
/// endpoint already use, so the dashboard cannot tell an analyst something the next click
/// contradicts:
/// </para>
/// <list type="bullet">
/// <item><c>canRescore</c> — <see cref="RescoreEligibility.Evaluate"/>, with the detail's exact inputs.</item>
/// <item>approvals and quorum — <see cref="IAnalysisApprovalQuorumService.GetTalliesAsync"/>, the batch
/// twin of the tally GET /api/analyses/{id} reads, shown only when <see cref="ApprovalTally.InForce"/>.</item>
/// <item>the persona suggestion and the self-confirmation rule — <see cref="PersonEvaluationJson.DecisionRules"/>,
/// the resolution PATCH .../decision enforces.</item>
/// <item>which optional integrations operations lists — <see cref="IIntegrationAccessService.ResolveUsableAsync"/>,
/// the same entitlement and availability gates the enrichment endpoints apply.</item>
/// <item>the current BCRA situación — <see cref="BcraSignalRule.Situacion"/>, the reading bcraSignals and
/// PYME_BCRA_WORSENED apply; the annual review — <see cref="AnnualReviewRule"/>, PYME_REVIEW_DUE's rule over
/// the same query; Vigente and Vencida — <see cref="CreditLineRules.EffectiveStatus(CreditLineOperation, DateTime)"/>.</item>
/// </list>
/// <para>
/// Sections run one after another, never in parallel: they share the request's FinanciaDbContext,
/// which is not thread-safe. Each does a fixed number of queries whatever its size.
/// </para>
/// </remarks>
public sealed partial class DashboardService : IDashboardService
{
    private const int PendingCap           = 20;
    private const int ReturnedCap          = 20;
    private const int AwaitingSignatureCap = 20;
    private const int RecentCap            = 8;
    private const int WaitingForMeCap      = 50;
    private const int DecidedByMeCap       = 10;
    private const int DecidedByMeDays      = 30;

    // The same fallback AnalysisController.ActorName uses for an actor id that no longer resolves.
    private const string UnknownActorName = "Usuario";

    private readonly IDashboardRepository           _dashboardRepository;
    private readonly IAnalysisApprovalQuorumService _approvalQuorumService;
    private readonly IEntityRepository              _entityRepository;
    private readonly IUserRepository                _userRepository;
    private readonly ITeamRepository                _teamRepository;
    private readonly IPersonAnalysisRepository      _personAnalysisRepository;
    private readonly IIntegrationAccessService      _integrationAccess;
    private readonly ITenantSettingsService         _tenantSettings;
    private readonly ISgrSettingsService            _sgrSettings;
    private readonly ILogger<DashboardService>      _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="DashboardService"/>.
    /// </summary>
    public DashboardService(
        IDashboardRepository           dashboardRepository,
        IAnalysisApprovalQuorumService approvalQuorumService,
        IEntityRepository              entityRepository,
        IUserRepository                userRepository,
        ITeamRepository                teamRepository,
        IPersonAnalysisRepository      personAnalysisRepository,
        IIntegrationAccessService      integrationAccess,
        ITenantSettingsService         tenantSettings,
        ISgrSettingsService            sgrSettings,
        ILogger<DashboardService>      logger)
    {
        _dashboardRepository      = dashboardRepository;
        _approvalQuorumService    = approvalQuorumService;
        _entityRepository         = entityRepository;
        _userRepository           = userRepository;
        _teamRepository           = teamRepository;
        _personAnalysisRepository = personAnalysisRepository;
        _integrationAccess        = integrationAccess;
        _tenantSettings           = tenantSettings;
        _sgrSettings              = sgrSettings;
        _logger                   = logger;
    }

    // ── my-work ───────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<MyWorkResponse> GetMyWorkAsync(
        string tenantId, User currentUser, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(currentUser);

        var userId   = currentUser.UserId;
        var degraded = new List<string>();

        var pending = await RunSectionAsync(DashboardSections.Pending, tenantId, userId, degraded,
            () => BuildPendingAsync(tenantId, userId, nowUtc, ct), ct).ConfigureAwait(false);

        var returned = await RunSectionAsync(DashboardSections.Returned, tenantId, userId, degraded,
            () => BuildReturnedAsync(tenantId, userId, nowUtc, ct), ct).ConfigureAwait(false);

        var bcraDegraded = await RunSectionAsync(DashboardSections.BcraDegraded, tenantId, userId, degraded,
            () => CountBcraDegradedAsync(tenantId, userId, ct), ct).ConfigureAwait(false);

        var awaiting = await RunSectionAsync(DashboardSections.AwaitingSignature, tenantId, userId, degraded,
            () => BuildAwaitingSignatureAsync(tenantId, userId, ct), ct).ConfigureAwait(false);

        var recent = await RunSectionAsync(DashboardSections.Recent, tenantId, userId, degraded,
            () => BuildRecentAsync(tenantId, userId, ct), ct).ConfigureAwait(false);

        var counts = new MyWorkCounts
        {
            Pending           = pending.Value?.TotalCount,
            Returned          = returned.Value?.TotalCount,
            BcraDegraded      = bcraDegraded.Ok ? bcraDegraded.Value : null,
            AwaitingSignature = awaiting.Value?.TotalCount
        };

        _logger.LogInformation(
            "Dashboard my-work for tenant {TenantId} user {UserId}: pending={Pending} returned={Returned} bcraDegraded={BcraDegraded} awaitingSignature={AwaitingSignature} recent={Recent} degradedSections={DegradedCount}",
            tenantId, userId, counts.Pending, counts.Returned, counts.BcraDegraded, counts.AwaitingSignature,
            recent.Value?.Count, degraded.Count);

        return new MyWorkResponse
        {
            GeneratedAt       = nowUtc,
            DegradedSections  = degraded,
            Counts            = counts,
            Pending           = pending.Value,
            Returned          = returned.Value,
            AwaitingSignature = awaiting.Value,
            Recent            = recent.Value
        };
    }

    private async Task<DashboardPage<MyWorkPendingItem>> BuildPendingAsync(
        string tenantId, Guid userId, DateTime nowUtc, CancellationToken ct)
    {
        var (rows, total) = await _dashboardRepository
            .GetMyPendingAsync(tenantId, userId, PendingCap, ct)
            .ConfigureAwait(false);

        var canRescore  = await ResolveCanRescoreAsync(tenantId, rows, ct).ConfigureAwait(false);
        var entities    = await EntityNamesAsync(tenantId, rows, ct).ConfigureAwait(false);
        var reliability = await ScoreReliabilityAsync(tenantId, rows, "my-work.pending", ct).ConfigureAwait(false);

        var items = rows
            .Select(r =>
            {
                entities.TryGetValue(r.EntityId, out var entity);
                return new MyWorkPendingItem
                {
                    ResultId         = r.ResultId,
                    AnalysisId       = r.AnalysisId,
                    AnalysisKind     = r.AnalysisKind,
                    EntityId         = r.EntityId,
                    EntityName       = entity?.BusinessName,
                    EntityCuit       = entity?.CUIT,
                    RequestedAmount  = r.RequestedAmount,
                    AiSuggestedLevel = r.AiSuggestedLevel,
                    AiSuggestedScore = r.AiSuggestedScore,
                    GeneratedAt      = r.GeneratedAt,
                    AgeDays          = AgeDays(r.GeneratedAt, nowUtc),
                    EnrichmentStatus = r.EnrichmentStatus,
                    CanRescore       = canRescore.Contains(r.AnalysisId),
                    ScoreReliability = reliability.GetValueOrDefault(r.ResultId)
                };
            })
            .ToList();

        return new DashboardPage<MyWorkPendingItem> { TotalCount = total, Items = items };
    }

    private async Task<DashboardPage<MyWorkReturnedItem>> BuildReturnedAsync(
        string tenantId, Guid userId, DateTime nowUtc, CancellationToken ct)
    {
        var (rows, total) = await _dashboardRepository
            .GetReturnedAsync(tenantId, userId, ReturnedCap, ct)
            .ConfigureAwait(false);

        var entities    = await EntityNamesAsync(tenantId, rows, ct).ConfigureAwait(false);
        var users       = await UsersAsync(tenantId, rows.Select(r => r.ReviewedBy), ct).ConfigureAwait(false);
        var reliability = await ScoreReliabilityAsync(tenantId, rows, "my-work.returned", ct).ConfigureAwait(false);

        var items = rows
            .Select(r =>
            {
                entities.TryGetValue(r.EntityId, out var entity);
                return new MyWorkReturnedItem
                {
                    ResultId         = r.ResultId,
                    AnalysisId       = r.AnalysisId,
                    AnalysisKind     = r.AnalysisKind,
                    EntityId         = r.EntityId,
                    EntityName       = entity?.BusinessName,
                    EntityCuit       = entity?.CUIT,
                    RequestedAmount  = r.RequestedAmount,
                    AiSuggestedLevel = r.AiSuggestedLevel,
                    AiSuggestedScore = r.AiSuggestedScore,
                    GeneratedAt      = r.GeneratedAt,
                    AgeDays          = AgeDays(r.GeneratedAt, nowUtc),
                    EnrichmentStatus = r.EnrichmentStatus,
                    ReviewedByName   = ActorName(r.ReviewedBy, users),
                    ReviewedAt       = r.ReviewedAt,
                    ReviewNotes      = r.ReviewNotes,
                    ScoreReliability = reliability.GetValueOrDefault(r.ResultId)
                };
            })
            .ToList();

        return new DashboardPage<MyWorkReturnedItem> { TotalCount = total, Items = items };
    }

    private async Task<int> CountBcraDegradedAsync(string tenantId, Guid userId, CancellationToken ct)
    {
        var rows = await _dashboardRepository
            .GetMyPendingDegradedAsync(tenantId, userId, ct)
            .ConfigureAwait(false);

        // Only the ones a re-score could actually fix: the same eligibility the pending items carry
        // as canRescore, so the badge never counts an analysis whose button is disabled.
        var canRescore = await ResolveCanRescoreAsync(tenantId, rows, ct).ConfigureAwait(false);
        return rows.Count(r => canRescore.Contains(r.AnalysisId));
    }

    private async Task<DashboardPage<MyWorkAwaitingSignatureItem>> BuildAwaitingSignatureAsync(
        string tenantId, Guid userId, CancellationToken ct)
    {
        var (rows, total) = await _dashboardRepository
            .GetAwaitingMySignatureAsync(tenantId, userId, AwaitingSignatureCap, ct)
            .ConfigureAwait(false);

        var tallies     = await TalliesAsync(tenantId, rows, ct).ConfigureAwait(false);
        var entities    = await EntityNamesAsync(tenantId, rows, ct).ConfigureAwait(false);
        var reliability = await ScoreReliabilityAsync(tenantId, rows, "my-work.awaiting-signature", ct).ConfigureAwait(false);

        var items = rows
            .Select(r =>
            {
                entities.TryGetValue(r.EntityId, out var entity);
                var (count, quorum) = TallyFields(tallies.GetValueOrDefault(r.AnalysisId));
                return new MyWorkAwaitingSignatureItem
                {
                    ResultId            = r.ResultId,
                    AnalysisId          = r.AnalysisId,
                    AnalysisKind        = r.AnalysisKind,
                    EntityId            = r.EntityId,
                    EntityName          = entity?.BusinessName,
                    EntityCuit          = entity?.CUIT,
                    AiSuggestedLevel    = r.AiSuggestedLevel,
                    AiSuggestedScore    = r.AiSuggestedScore,
                    PreApprovedAt       = r.PreApprovedAt,
                    PreApprovedAmount   = r.PreApprovedAmount,
                    PreApprovedCurrency = r.PreApprovedCurrency,
                    ApprovalCount       = count,
                    ApprovalQuorum      = quorum,
                    ScoreReliability    = reliability.GetValueOrDefault(r.ResultId)
                };
            })
            .ToList();

        return new DashboardPage<MyWorkAwaitingSignatureItem> { TotalCount = total, Items = items };
    }

    private async Task<IReadOnlyList<MyWorkRecentItem>> BuildRecentAsync(
        string tenantId, Guid userId, CancellationToken ct)
    {
        var rows = await _dashboardRepository
            .GetMyRecentAsync(tenantId, userId, RecentCap, ct)
            .ConfigureAwait(false);

        var entities    = await EntityNamesAsync(tenantId, rows, ct).ConfigureAwait(false);
        var reliability = await ScoreReliabilityAsync(tenantId, rows, "my-work.recent", ct).ConfigureAwait(false);

        return rows
            .Select(r =>
            {
                entities.TryGetValue(r.EntityId, out var entity);
                return new MyWorkRecentItem
                {
                    ResultId         = r.ResultId,
                    AnalysisId       = r.AnalysisId,
                    AnalysisKind     = r.AnalysisKind,
                    EntityId         = r.EntityId,
                    EntityName       = entity?.BusinessName,
                    EntityCuit       = entity?.CUIT,
                    Status           = r.Status,
                    AiSuggestedLevel = r.AiSuggestedLevel,
                    AiSuggestedScore = r.AiSuggestedScore,
                    CreatedAt        = r.CreatedAt,
                    GeneratedAt      = r.GeneratedAt,
                    ScoreReliability = reliability.GetValueOrDefault(r.ResultId)
                };
            })
            .ToList();
    }

    // ── approvals ─────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<ApprovalsResponse> GetApprovalsAsync(
        string tenantId, User currentUser, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(currentUser);

        var userId   = currentUser.UserId;
        var degraded = new List<string>();

        var waiting = await RunSectionAsync(DashboardSections.WaitingForMe, tenantId, userId, degraded,
            () => BuildWaitingForMeAsync(tenantId, userId, nowUtc, ct), ct).ConfigureAwait(false);

        var decided = await RunSectionAsync(DashboardSections.DecidedByMe, tenantId, userId, degraded,
            () => BuildDecidedByMeAsync(tenantId, userId, nowUtc, ct), ct).ConfigureAwait(false);

        var counts = new ApprovalsCounts
        {
            WaitingForMe        = waiting.Value?.Page.TotalCount,
            SuggestedRejections = waiting.Value?.SuggestedRejections
        };

        _logger.LogInformation(
            "Dashboard approvals for tenant {TenantId} user {UserId}: waitingForMe={WaitingForMe} suggestedRejections={SuggestedRejections} decidedByMe={DecidedByMe} degradedSections={DegradedCount}",
            tenantId, userId, counts.WaitingForMe, counts.SuggestedRejections, decided.Value?.Count, degraded.Count);

        return new ApprovalsResponse
        {
            GeneratedAt      = nowUtc,
            DegradedSections = degraded,
            Counts           = counts,
            WaitingForMe     = waiting.Value?.Page,
            DecidedByMe      = decided.Value
        };
    }

    private sealed record WaitingForMeSection(DashboardPage<WaitingForMeItem> Page, int SuggestedRejections);

    private async Task<WaitingForMeSection> BuildWaitingForMeAsync(
        string tenantId, Guid userId, DateTime nowUtc, CancellationToken ct)
    {
        // The WHOLE PreApproved set, not a page of it: the exclusions below run in memory, and capping
        // before them would make totalCount — and the suggestedRejections badge — wrong.
        var rows = await _dashboardRepository
            .GetPreApprovedAsync(tenantId, ct)
            .ConfigureAwait(false);

        var analysisIds = rows.Select(r => r.AnalysisId).ToList();

        var alreadyApproved = await _dashboardRepository
            .GetAnalysisIdsApprovedByAsync(tenantId, userId, analysisIds, ct)
            .ConfigureAwait(false);

        // Persona humana inputs, read only when the set holds a persona analysis at all.
        var personaIds = rows
            .Where(r => string.Equals(r.AnalysisKind, AnalysisKinds.PersonaHumana, StringComparison.Ordinal))
            .Select(r => r.AnalysisId)
            .ToList();

        IReadOnlyDictionary<Guid, string>  solicitanteFilters = new Dictionary<Guid, string>();
        IReadOnlyDictionary<string, decimal> personConfig     = new Dictionary<string, decimal>();
        if (personaIds.Count > 0)
        {
            solicitanteFilters = await _dashboardRepository
                .GetSolicitanteFiltersAsync(tenantId, personaIds, ct)
                .ConfigureAwait(false);

            personConfig = await _personAnalysisRepository
                .GetEvaluationConfigAsync(tenantId, ct)
                .ConfigureAwait(false);
        }

        var eligible = new List<(DashboardAnalysisRow Row, string? Suggestion)>();
        foreach (var row in rows)
        {
            // A signature already on record: voting again is a no-op, so there is nothing to ask for.
            if (alreadyApproved.Contains(row.AnalysisId))
                continue;

            string? suggestion = null;
            if (string.Equals(row.AnalysisKind, AnalysisKinds.PersonaHumana, StringComparison.Ordinal))
            {
                var filtersJson = solicitanteFilters.GetValueOrDefault(row.AnalysisId);

                // The decision endpoint's rule, resolved by the same code: on a persona humana analysis
                // whose rules demand a pre-approval (a suggested rejection under
                // pf_rechazo_sugerido_requiere_preaprobacion), whoever pre-approved it may not confirm
                // it — PATCH .../decision answers 403 self_confirmation_not_allowed. A balance analysis
                // has no such rule, and is deliberately not excluded.
                var rules = PersonEvaluationJson.DecisionRules(personConfig, filtersJson);
                if (rules.RequiresPreApproval && row.PreApprovedBy == userId)
                    continue;

                // As the detail shows it: derived from the solicitante's filters, null without one.
                suggestion = filtersJson is null
                    ? null
                    : PersonEvaluationService.Suggest(PersonEvaluationJson.FiltersOf(filtersJson));
            }

            eligible.Add((row, suggestion));
        }

        var suggestedRejections = eligible.Count(e =>
            string.Equals(e.Suggestion, PersonSuggestions.RechazoSugerido, StringComparison.Ordinal));

        var page     = eligible.Take(WaitingForMeCap).ToList();
        var pageRows = page.Select(e => e.Row).ToList();

        var tallies  = await TalliesAsync(tenantId, pageRows, ct).ConfigureAwait(false);
        var entities = await EntityNamesAsync(tenantId, pageRows, ct).ConfigureAwait(false);
        var users    = await UsersAsync(tenantId, pageRows.Select(r => r.PreApprovedBy), ct).ConfigureAwait(false);
        var myTeams  = await TeamsManagedByAsync(tenantId, userId, users.Values, ct).ConfigureAwait(false);

        // The confirmation queue is where the verdict matters most — read for the capped page only, never
        // for the whole PreApproved set loaded above.
        var reliability = await ScoreReliabilityAsync(tenantId, pageRows, "approvals.waiting-for-me", ct).ConfigureAwait(false);

        var items = page
            .Select(e =>
            {
                var r = e.Row;
                entities.TryGetValue(r.EntityId, out var entity);
                var (count, quorum) = TallyFields(tallies.GetValueOrDefault(r.AnalysisId));
                var preApprover     = r.PreApprovedBy is Guid id ? users.GetValueOrDefault(id) : null;

                return new WaitingForMeItem
                {
                    ResultId            = r.ResultId,
                    AnalysisId          = r.AnalysisId,
                    AnalysisKind        = r.AnalysisKind,
                    EntityId            = r.EntityId,
                    EntityName          = entity?.BusinessName,
                    EntityCuit          = entity?.CUIT,
                    PreApprovedAmount   = r.PreApprovedAmount,
                    PreApprovedCurrency = r.PreApprovedCurrency,
                    PreApprovedBy       = r.PreApprovedBy,
                    PreApprovedByName   = ActorName(r.PreApprovedBy, users),
                    PreApprovedAt       = r.PreApprovedAt,
                    AgeDays             = r.PreApprovedAt is DateTime at ? AgeDays(at, nowUtc) : 0,
                    AiSuggestedLevel    = r.AiSuggestedLevel,
                    AiSuggestedScore    = r.AiSuggestedScore,
                    PersonSuggestion    = e.Suggestion,
                    ApprovalCount       = count,
                    ApprovalQuorum      = quorum,
                    InMyTeam            = preApprover?.TeamId is Guid teamId && myTeams.Contains(teamId),
                    ScoreReliability    = reliability.GetValueOrDefault(r.ResultId)
                };
            })
            .ToList();

        return new WaitingForMeSection(
            new DashboardPage<WaitingForMeItem> { TotalCount = eligible.Count, Items = items },
            suggestedRejections);
    }

    private async Task<IReadOnlyList<DecidedByMeItem>> BuildDecidedByMeAsync(
        string tenantId, Guid userId, DateTime nowUtc, CancellationToken ct)
    {
        var sinceUtc = nowUtc.AddDays(-DecidedByMeDays);

        var votes = await _dashboardRepository
            .GetVotesByUserSinceAsync(tenantId, userId, sinceUtc, ct)
            .ConfigureAwait(false);

        var reviewed = await _dashboardRepository
            .GetReviewedByUserSinceAsync(tenantId, userId, sinceUtc, ct)
            .ConfigureAwait(false);

        // Only acts the rows PROVE. Votes are wiped when an analysis is returned or re-pre-approved, so
        // a signature given before either is gone from the record; and a row back in PreApproved whose
        // ReviewedBy is still the caller cannot say whether that earlier act was a return, a rejection or
        // an approval — so it is left out rather than guessed. Rank 1 beats rank 0 at the same instant:
        // the vote that crossed a quorum and the confirmation it caused share a timestamp, and the act is
        // the confirmation.
        var candidates = new List<(Guid AnalysisId, string Act, DateTime At, int Rank)>();

        foreach (var vote in votes)
        {
            var act = vote.Vote switch
            {
                AnalysisVoteValues.Approved => DashboardActs.ApprovalVote,
                AnalysisVoteValues.Rejected => DashboardActs.Rejected,
                _                           => null
            };
            if (act is not null) candidates.Add((vote.AnalysisId, act, vote.VotedAt, 0));
        }

        foreach (var row in reviewed)
        {
            var act = ActOf(row);
            if (act is not null && row.ReviewedAt is DateTime at) candidates.Add((row.AnalysisId, act, at, 1));
        }

        var latest = candidates
            .GroupBy(c => c.AnalysisId)
            .Select(g => g.OrderByDescending(c => c.At).ThenByDescending(c => c.Rank).First())
            .OrderByDescending(c => c.At)
            .ThenBy(c => c.AnalysisId)
            .ToList();

        if (latest.Count == 0)
            return [];

        // The rows behind the acts — one read, then the cap applied AFTER it, so an act on an analysis
        // with no result row (invisible to the Portal) does not take one of the ten places.
        var rowsById = (await _dashboardRepository
                .GetByAnalysisIdsAsync(tenantId, latest.Select(c => c.AnalysisId).ToList(), ct)
                .ConfigureAwait(false))
            .ToDictionary(r => r.AnalysisId);

        var picked = latest
            .Where(c => rowsById.ContainsKey(c.AnalysisId))
            .Take(DecidedByMeCap)
            .ToList();

        var entities = await EntityNamesAsync(tenantId, picked.Select(c => rowsById[c.AnalysisId]).ToList(), ct)
            .ConfigureAwait(false);

        return picked
            .Select(c =>
            {
                var r = rowsById[c.AnalysisId];
                entities.TryGetValue(r.EntityId, out var entity);
                return new DecidedByMeItem
                {
                    ResultId     = r.ResultId,
                    AnalysisId   = r.AnalysisId,
                    AnalysisKind = r.AnalysisKind,
                    EntityId     = r.EntityId,
                    EntityName   = entity?.BusinessName,
                    EntityCuit   = entity?.CUIT,
                    Act          = c.Act,
                    ActedAt      = c.At,
                    Status       = r.Status
                };
            })
            .ToList();
    }

    // ── Shared helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs one section, isolating its failure. Cancellation is not a section failure and propagates.
    /// The log carries the section name and the ids — never a name, CUIT, amount or note.
    /// </summary>
    /// <remarks>
    /// <paramref name="userId"/> is the caller for the caller-scoped dashboards and null for the
    /// tenant-wide ones (portfolio, oversight, operations), which read no user row.
    /// </remarks>
    private async Task<(bool Ok, T? Value)> RunSectionAsync<T>(
        string section, string tenantId, Guid? userId, List<string> degraded, Func<Task<T>> build, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            return (true, await build().ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Dashboard section {Section} failed for tenant {TenantId} user {UserId}; it is returned as null and listed in degradedSections.",
                section, tenantId, userId);

            degraded.Add(section);
            return (false, default);
        }
    }

    /// <summary>
    /// The analyses among <paramref name="rows"/> that may be re-scored now: the detail's
    /// <c>canRescore</c> computation, batched. Row rules first, with no document status; the document
    /// is read only for the rows that pass them, and then the full rule set decides — exactly the
    /// two-step order of AnalysisController.ApplyRescoreFieldsAsync.
    /// </summary>
    private async Task<IReadOnlySet<Guid>> ResolveCanRescoreAsync(
        string tenantId, IReadOnlyList<DashboardAnalysisRow> rows, CancellationToken ct)
    {
        var passing = rows
            .Where(r => RescoreEligibility.Evaluate(r.ToAnalysis(), r.HasBalance, hasVotes: false, documentProcessingStatus: null) is null)
            .ToList();

        var documentIds = passing
            .Where(r => r.DocumentId.HasValue)
            .Select(r => r.DocumentId!.Value)
            .ToList();

        var statuses = await _dashboardRepository
            .GetDocumentStatusesAsync(tenantId, documentIds, ct)
            .ConfigureAwait(false);

        return passing
            .Where(r => r.DocumentId is not Guid documentId
                     || RescoreEligibility.Evaluate(r.ToAnalysis(), r.HasBalance, hasVotes: false,
                            statuses.GetValueOrDefault(documentId)) is null)
            .Select(r => r.AnalysisId)
            .ToHashSet();
    }

    /// <summary>The tallies for PreApproved rows, in one batch.</summary>
    private async Task<IReadOnlyDictionary<Guid, ApprovalTally>> TalliesAsync(
        string tenantId, IReadOnlyList<DashboardAnalysisRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
            return new Dictionary<Guid, ApprovalTally>();

        return await _approvalQuorumService
            .GetTalliesAsync(tenantId, rows.Select(r => r.ToAnalysis()).ToList(), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The count and quorum to show, gated exactly like AnalysisController.ApplyApprovalTally: absent —
    /// not zeroed — when no quorum is in force, so a tenant whose teams name no managers sees no tally.
    /// </summary>
    private static (int? Count, int? Quorum) TallyFields(ApprovalTally? tally)
        => tally is { InForce: true } ? (tally.Approved, tally.Required) : (null, null);

    /// <summary>Entity names and CUITs for the rows, tenant-scoped, in one query.</summary>
    private Task<IReadOnlyDictionary<Guid, EntityNameProjection>> EntityNamesAsync(
        string tenantId, IReadOnlyList<DashboardAnalysisRow> rows, CancellationToken ct)
        => EntityNamesAsync(tenantId, rows.Select(r => r.EntityId), ct);

    /// <summary>Entity names and CUITs for the given ids, tenant-scoped, in one query.</summary>
    private async Task<IReadOnlyDictionary<Guid, EntityNameProjection>> EntityNamesAsync(
        string tenantId, IEnumerable<Guid> entityIds, CancellationToken ct)
    {
        var ids = entityIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, EntityNameProjection>();

        return await _entityRepository
            .GetNameLookupByIdsAsync(ids, tenantId, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The users behind the given ids, in one query, keyed by UserId. Only rows of this tenant are
    /// kept: GetByIdsAsync carries no tenant predicate, and the ids are only as tenant-safe as the rows
    /// they came from.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, User>> UsersAsync(
        string tenantId, IEnumerable<Guid?> userIds, CancellationToken ct)
    {
        var ids = userIds
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        if (ids.Count == 0)
            return new Dictionary<Guid, User>();

        var users = await _userRepository.GetByIdsAsync(ids, ct).ConfigureAwait(false);

        return users
            .Where(u => string.Equals(u.TenantId, tenantId, StringComparison.Ordinal))
            .GroupBy(u => u.UserId)
            .ToDictionary(g => g.Key, g => g.First());
    }

    /// <summary>Display name for an actor id, with AnalysisController.ActorName's fallback.</summary>
    private static string? ActorName(Guid? actorId, IReadOnlyDictionary<Guid, User> users)
    {
        if (actorId is null)
            return null;

        return users.TryGetValue(actorId.Value, out var user) && !string.IsNullOrWhiteSpace(user.DisplayName)
            ? user.DisplayName
            : UnknownActorName;
    }

    /// <summary>
    /// The teams, among those of <paramref name="preApprovers"/>, that name <paramref name="userId"/>
    /// as a manager — in [dbo].[TeamManagers] or in the deprecated Teams.ManagerUserId, the same union
    /// TeamManagerResolution reads. Two queries, and none when no pre-approver has a team.
    /// </summary>
    private async Task<IReadOnlySet<Guid>> TeamsManagedByAsync(
        string tenantId, Guid userId, IEnumerable<User> preApprovers, CancellationToken ct)
    {
        var teamIds = preApprovers
            .Where(u => u.TeamId.HasValue)
            .Select(u => u.TeamId!.Value)
            .ToHashSet();

        if (teamIds.Count == 0)
            return new HashSet<Guid>();

        var managersByTeam = await _teamRepository
            .GetManagerUserIdsByTenantAsync(tenantId, ct)
            .ConfigureAwait(false);

        var teams = await _teamRepository
            .GetByTenantAsync(tenantId, ct)
            .ConfigureAwait(false);

        var managed = new HashSet<Guid>();
        foreach (var teamId in teamIds)
        {
            if (managersByTeam.TryGetValue(teamId, out var managers) && managers.Contains(userId))
                managed.Add(teamId);
        }

        foreach (var team in teams)
        {
            if (teamIds.Contains(team.TeamId) && team.ManagerUserId == userId)
                managed.Add(team.TeamId);
        }

        return managed;
    }

    /// <summary>
    /// The act a reviewed row PROVES — read off its status now, for a row whose ReviewedBy is set:
    /// Approved is a confirmation when a pre-approval is on the row and a one-step approval otherwise,
    /// Rejected a rejection, PendingReview a return. A PreApproved row with a reviewer cannot say what the
    /// earlier act was, and yields null rather than a guess.
    /// </summary>
    private static string? ActOf(DashboardAnalysisRow row) => row.Status switch
    {
        AnalysisStatuses.Approved      => row.PreApprovedAt is not null ? DashboardActs.Confirmed : DashboardActs.Approved,
        AnalysisStatuses.Rejected      => DashboardActs.Rejected,
        AnalysisStatuses.PendingReview => DashboardActs.Returned,
        _                              => null
    };

    /// <summary>Whole days from <paramref name="sinceUtc"/> to <paramref name="nowUtc"/>, floored, never negative.</summary>
    private static int AgeDays(DateTime sinceUtc, DateTime nowUtc)
        => Math.Max(0, (int)Math.Floor((nowUtc - sinceUtc).TotalDays));
}
