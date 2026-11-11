// Project : Api.Web.FinanceIA
// Created : 2026-10-30

using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Default implementation of <see cref="IPersonCalibrationService"/>. Registered Scoped, like the repository it
/// reads through.
/// </summary>
/// <remarks>
/// <para>
/// NOTHING HERE RE-IMPLEMENTS A RULE OR A READER. Every frozen column is read with
/// <see cref="PersonEvaluationJson.TryDeserialize{T}"/>; the suggestion is <see cref="PersonEvaluationService.Suggest"/>;
/// the fiador rows are <see cref="PersonEvaluationJson.FiadorRowsOf"/> and their aggregate coverage is
/// <see cref="PersonEvaluationJson.CounterGuaranteeCoverageOf"/> — called with the minimum FROZEN in the analysis'
/// own parameters, so a threshold the tenant moved since does not rewrite history. Unknown JSON members (a newer
/// engine's) are ignored by those readers.
/// </para>
/// <para>
/// The population — the analyses and their participant blobs — is loaded ONCE, on first use, and shared by the
/// sections. Sections run one after another, never in parallel: they share the request's FinanciaDbContext.
/// </para>
/// <para>
/// NO PII IN LOGS. Ids and counts only: never an entity name, a CUIT or a reason.
/// </para>
/// </remarks>
public sealed partial class PersonCalibrationService : IPersonCalibrationService
{
    private const int FlaggedCasesCap      = 200;
    private const int RecentReevaluations  = 20;
    private const int PilotAnalysesTarget  = 20;
    private const int PilotDaysTarget      = 30;

    // The same fallback DashboardService and AnalysisController use for an actor id that no longer resolves.
    private const string UnknownActorName = "Usuario";

    private readonly IPersonCalibrationRepository      _repository;
    private readonly IEntityRepository                 _entityRepository;
    private readonly IUserRepository                   _userRepository;
    private readonly ITeamRepository                   _teamRepository;
    private readonly ILogger<PersonCalibrationService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="PersonCalibrationService"/>.
    /// </summary>
    public PersonCalibrationService(
        IPersonCalibrationRepository      repository,
        IEntityRepository                 entityRepository,
        IUserRepository                   userRepository,
        ITeamRepository                   teamRepository,
        ILogger<PersonCalibrationService> logger)
    {
        _repository       = repository;
        _entityRepository = entityRepository;
        _userRepository   = userRepository;
        _teamRepository   = teamRepository;
        _logger           = logger;
    }

    // ── Population ────────────────────────────────────────────────────────────────

    /// <summary>The analyses in range, the readable ones parsed, and the ids of the unreadable ones.</summary>
    private sealed record Population(
        IReadOnlyList<PersonCalibrationAnalysisRow> Analyses,
        IReadOnlyList<CalibrationCase> Cases,
        IReadOnlyList<Guid> Unreadable)
    {
        public IReadOnlyDictionary<Guid, CalibrationCase> ByAnalysis { get; } =
            Cases.ToDictionary(c => c.Row.AnalysisId);
    }

    /// <summary>One readable analysis, its frozen solicitante evaluation and everything derived from it.</summary>
    private sealed record CalibrationCase(
        PersonCalibrationAnalysisRow Row,
        AnalysisParticipant Solicitante,
        IReadOnlyList<PersonFilterResult> Filters,
        IReadOnlyList<PersonIndicatorResult> Indicators,
        PersonCoverageResult Coverage,
        PersonEvaluationSources Sources,
        string Suggestion,
        string Outcome,
        int? FrozenMinPct,
        CounterGuaranteeCoverage? CounterGuarantee,
        IReadOnlyList<string> RechazoFilterIds,
        IReadOnlyList<string> RojoIndicatorIds,
        PersonCalibrationFlags Flags)
    {
        /// <summary>
        /// Listed among the flagged cases: an approval in red or against the suggestion, a rejection in green, or
        /// an approval whose counter-guarantees fall short of the frozen minimum.
        /// </summary>
        public bool IsFlagged =>
            Flags.ApprovedWithRechazoSugerido
            || Flags.ApprovedWithRedBand
            || Flags.RejectedInGreen
            || (IsApproving(Row.Status) && Flags.CounterGuaranteeBelowMin == true);
    }

    private async Task<Population> LoadPopulationAsync(
        string tenantId, PersonCalibrationPeriod period, Guid? teamId, CancellationToken ct)
    {
        var analyses = await _repository
            .GetAnalysesAsync(tenantId, period.StartUtc, period.EndUtc, teamId, ct)
            .ConfigureAwait(false);

        // Participant blobs are loaded only for the persona analyses in range, in one query.
        var participants = await _repository
            .GetParticipantsAsync(tenantId, analyses.Select(a => a.AnalysisId).ToList(), ct)
            .ConfigureAwait(false);

        var byAnalysis = participants.ToLookup(p => p.AnalysisId);

        var cases      = new List<CalibrationCase>(analyses.Count);
        var unreadable = new List<Guid>();

        foreach (var row in analyses)
        {
            CalibrationCase? parsed;
            try
            {
                parsed = Parse(row, byAnalysis[row.AnalysisId].ToList());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A frozen blob shaped in a way the readers did not anticipate. The row is skipped like an
                // unparseable one; the exception carries no blob content into the log.
                _logger.LogWarning(ex,
                    "Person calibration could not derive analysis {AnalysisId} for tenant {TenantId}; it is counted as unreadable.",
                    row.AnalysisId, tenantId);
                parsed = null;
            }

            if (parsed is null)
                unreadable.Add(row.AnalysisId);
            else
                cases.Add(parsed);
        }

        if (unreadable.Count > 0)
        {
            _logger.LogWarning(
                "Person calibration for tenant {TenantId} skipped {UnreadableCount} unreadable analyses (no solicitante row or unreadable frozen JSON): {AnalysisIds}",
                tenantId, unreadable.Count, string.Join(",", unreadable));
        }

        return new Population(analyses, cases, unreadable);
    }

    /// <summary>
    /// Reads one analysis. Null when it has no solicitante row or any of the solicitante's four frozen
    /// columns does not parse — <see cref="PersonEvaluationJson.FiltersOf"/> would read an unparseable column
    /// as "no filters", which is exactly what the board must not silently count as a clean evaluation.
    /// </summary>
    private static CalibrationCase? Parse(PersonCalibrationAnalysisRow row, IReadOnlyList<AnalysisParticipant> participants)
    {
        // The earliest solicitante row: the one GetSolicitanteFiltersAsync and the detail read.
        var solicitante = participants
            .Where(p => p.Role == ParticipantRoles.Solicitante)
            .OrderBy(p => p.CreatedAt)
            .ThenBy(p => p.ParticipantId)
            .FirstOrDefault();
        if (solicitante is null)
            return null;

        var filters    = PersonEvaluationJson.TryDeserialize<List<PersonFilterResult>>(solicitante.FiltersJson);
        var indicators = PersonEvaluationJson.TryDeserialize<List<PersonIndicatorResult>>(solicitante.IndicatorsJson);
        var coverage   = PersonEvaluationJson.TryDeserialize<PersonCoverageResult>(solicitante.CoverageJson);
        var sources    = PersonEvaluationJson.TryDeserialize<PersonEvaluationSources>(solicitante.DocumentVersionsJson);

        if (filters is null || indicators is null || coverage is null || sources is null
            || filters.Any(f => f is null) || indicators.Any(i => i is null))
        {
            return null;
        }

        var suggestion = PersonEvaluationService.Suggest(filters);
        var frozenMin  = sources.Parameters?.ContragarantiaCoberturaMinPct;

        // The minimum only moves MeetsMin; the ratio is the same whatever it is. Without a frozen minimum the
        // coverage is still shown, and the flag stays null.
        var fiadores = PersonEvaluationJson.FiadorRowsOf(participants);
        var counter  = PersonEvaluationJson.CounterGuaranteeCoverageOf(solicitante, fiadores, frozenMin ?? 0);

        bool? belowMin = counter is { Ratio: not null } && frozenMin is not null
            ? !counter.MeetsMin
            : null;

        var rechazoIds = filters
            .Where(f => f.Outcome == PersonFilterOutcomes.Rechazo)
            .Select(f => f.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var rojoIds = indicators
            .Where(i => i.Band == PersonIndicatorBands.Rojo)
            .Select(i => i.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var approving = IsApproving(row.Status);

        var flags = new PersonCalibrationFlags
        {
            ApprovedWithRechazoSugerido = approving && suggestion == PersonSuggestions.RechazoSugerido,
            ApprovedWithRedBand         = approving && rojoIds.Count > 0,
            RejectedInGreen             = row.Status == AnalysisStatuses.Rejected
                                          && suggestion == PersonSuggestions.ParaDecisionDelAnalista
                                          && rojoIds.Count == 0,
            CounterGuaranteeBelowMin    = belowMin
        };

        return new CalibrationCase(
            row, solicitante, filters, indicators, coverage, sources, suggestion, OutcomeOf(row.Status),
            frozenMin, counter, rechazoIds, rojoIds, flags);
    }

    // ── Rules shared by the board and the CSV ─────────────────────────────────────

    /// <summary>The analyst's approving act: a settled approval, or a pre-approval awaiting confirmation.</summary>
    private static bool IsApproving(string status)
        => status is AnalysisStatuses.Approved or AnalysisStatuses.PreApproved;

    private static string OutcomeOf(string status) => status switch
    {
        AnalysisStatuses.Approved      => PersonCalibrationOutcomes.Approved,
        AnalysisStatuses.Rejected      => PersonCalibrationOutcomes.Rejected,
        AnalysisStatuses.PreApproved   => PersonCalibrationOutcomes.PreApproved,
        AnalysisStatuses.PendingReview => PersonCalibrationOutcomes.Pending,
        _                              => PersonCalibrationOutcomes.Other
    };

    /// <summary>
    /// Who took the act the status records, and when: the reviewer of an Approved or Rejected analysis — or of
    /// a PendingReview one a confirmer returned — and the pre-approver of a PreApproved one.
    /// </summary>
    private static (Guid? By, DateTime? At) DeciderOf(PersonCalibrationAnalysisRow row) => row.Status switch
    {
        AnalysisStatuses.PreApproved => (row.PreApprovedBy, row.PreApprovedAt),
        AnalysisStatuses.Approved or AnalysisStatuses.Rejected or AnalysisStatuses.PendingReview
            => (row.ReviewedBy, row.ReviewedAt),
        _ => (null, null)
    };

    /// <summary>
    /// The reason of the act: the reason of the checks that act wrote — the final approval writes
    /// ConfirmationChecksJson, a pre-approval PreApprovalChecksJson, a rejection none — else ReviewNotes.
    /// </summary>
    private static string? ReasonOf(PersonCalibrationAnalysisRow row)
    {
        var checksJson = row.Status switch
        {
            AnalysisStatuses.Approved    => row.ConfirmationChecksJson,
            AnalysisStatuses.PreApproved => row.PreApprovalChecksJson,
            _                            => null
        };

        var checksReason = PersonEvaluationJson.TryDeserialize<AnalysisDecisionChecks>(checksJson)?.Reason;

        return !string.IsNullOrWhiteSpace(checksReason) ? checksReason
             : !string.IsNullOrWhiteSpace(row.ReviewNotes) ? row.ReviewNotes
             : null;
    }

    // ── Shared helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs one section, isolating its failure — DashboardService.RunSectionAsync's contract. Cancellation is
    /// not a section failure and propagates. The log carries the section name and the tenant only.
    /// </summary>
    private async Task<(bool Ok, T? Value)> RunSectionAsync<T>(
        string section, string tenantId, List<string> degraded, Func<Task<T>> build, CancellationToken ct)
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
                "Person calibration section {Section} failed for tenant {TenantId}; it is returned as null and listed in degradedSections.",
                section, tenantId);

            degraded.Add(section);
            return (false, default);
        }
    }

    /// <summary>Entity names and CUITs, tenant-scoped, in one query.</summary>
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
    /// The users behind the given ids, keyed by UserId, this tenant's only: GetByIdsAsync carries no tenant
    /// predicate (DashboardService.UsersAsync's rule).
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

    /// <summary>The tenant's team names, keyed by TeamId.</summary>
    private async Task<IReadOnlyDictionary<Guid, string>> TeamNamesAsync(string tenantId, CancellationToken ct)
    {
        var teams = await _teamRepository.GetByTenantAsync(tenantId, ct).ConfigureAwait(false);

        return teams
            .Where(t => string.Equals(t.TenantId, tenantId, StringComparison.Ordinal))
            .GroupBy(t => t.TeamId)
            .ToDictionary(g => g.Key, g => g.First().Name);
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

    /// <summary>The requester's CURRENT team id and name.</summary>
    private static (Guid? TeamId, string? TeamName) TeamOf(
        Guid requestedBy, IReadOnlyDictionary<Guid, User> users, IReadOnlyDictionary<Guid, string> teams)
    {
        if (!users.TryGetValue(requestedBy, out var user) || user.TeamId is not { } teamId)
            return (null, null);

        return (teamId, teams.GetValueOrDefault(teamId));
    }
}
