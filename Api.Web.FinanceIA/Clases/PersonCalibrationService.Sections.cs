// Project : Api.Web.FinanceIA
// Created : 2026-10-30

using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;

namespace Api.Web.FinanceIA.Clases;

// GET /api/dashboard/person-calibration: the seven sections, each isolated like the dashboard's.
public sealed partial class PersonCalibrationService
{
    private const string SinDatosBand = "SinDatos";
    private const string SinBandaBand = "SinBanda";

    private static readonly string[] Suggestions =
        [PersonSuggestions.RechazoSugerido, PersonSuggestions.ParaDecisionDelAnalista];

    private static readonly string[] BandOrder =
        [PersonIndicatorBands.Verde, PersonIndicatorBands.Amarillo, PersonIndicatorBands.Naranja, PersonIndicatorBands.Rojo, SinDatosBand, SinBandaBand];

    /// <inheritdoc />
    public async Task<PersonCalibrationResponse> GetBoardAsync(
        string tenantId, PersonCalibrationPeriod period, Guid? teamId, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var degraded = new List<string>();

        // Loaded on first use and shared: when the load itself fails, every section that needs it degrades
        // with it, and the response is still a 200.
        Task<Population>? populationTask = null;
        Task<Population> PopulationAsync() => populationTask ??= LoadPopulationAsync(tenantId, period, teamId, ct);

        var summary = await RunSectionAsync(PersonCalibrationSections.Summary, tenantId, degraded,
            async () => BuildSummary(await PopulationAsync().ConfigureAwait(false),
                await _repository.GetPilotMilestoneAsync(tenantId, ct).ConfigureAwait(false), nowUtc), ct).ConfigureAwait(false);

        var decisions = await RunSectionAsync(PersonCalibrationSections.Decisions, tenantId, degraded,
            async () => await BuildDecisionsAsync(tenantId, await PopulationAsync().ConfigureAwait(false), ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        var filters = await RunSectionAsync(PersonCalibrationSections.Filters, tenantId, degraded,
            async () => BuildFilters(await PopulationAsync().ConfigureAwait(false)), ct).ConfigureAwait(false);

        var indicators = await RunSectionAsync(PersonCalibrationSections.Indicators, tenantId, degraded,
            async () => BuildIndicators(await PopulationAsync().ConfigureAwait(false)), ct).ConfigureAwait(false);

        var documents = await RunSectionAsync(PersonCalibrationSections.Documents, tenantId, degraded,
            async () => BuildDocuments(await PopulationAsync().ConfigureAwait(false)), ct).ConfigureAwait(false);

        var reevaluations = await RunSectionAsync(PersonCalibrationSections.Reevaluations, tenantId, degraded,
            async () => await BuildReevaluationsAsync(tenantId, await PopulationAsync().ConfigureAwait(false), ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        var parameters = await RunSectionAsync(PersonCalibrationSections.Parameters, tenantId, degraded,
            async () => await BuildParametersAsync(tenantId, await PopulationAsync().ConfigureAwait(false), ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        Population? population = populationTask is { IsCompletedSuccessfully: true }
            ? await populationTask.ConfigureAwait(false)
            : null;

        _logger.LogInformation(
            "Person calibration for tenant {TenantId} from {From} to {To} team {TeamId}: analyses={Analyses} readable={Readable} unreadable={Unreadable} flagged={Flagged} degradedSections={DegradedCount}",
            tenantId, period.From, period.To, teamId, population?.Analyses.Count, population?.Cases.Count,
            population?.Unreadable.Count, decisions.Value?.FlaggedCases.TotalCount, degraded.Count);

        return new PersonCalibrationResponse
        {
            GeneratedAt      = nowUtc,
            From             = period.From,
            To               = period.To,
            TeamId           = teamId,
            TeamAttribution  = PersonCalibrationTeamAttributions.CurrentMembership,
            DegradedSections = degraded,
            UnreadableRows   = population is null
                ? null
                : new PersonCalibrationUnreadable { Count = population.Unreadable.Count, AnalysisIds = population.Unreadable },
            Summary          = summary.Value,
            Decisions        = decisions.Value,
            Filters          = filters.Value,
            Indicators       = indicators.Value,
            Documents        = documents.Value,
            Reevaluations    = reevaluations.Value,
            Parameters       = parameters.Value
        };
    }

    // ── summary ───────────────────────────────────────────────────────────────────

    private static PersonCalibrationSummary BuildSummary(
        Population population, PersonCalibrationMilestoneRow milestone, DateTime nowUtc)
    {
        var decided = population.Analyses.Count(a => a.Status is AnalysisStatuses.Approved or AnalysisStatuses.Rejected);

        return new PersonCalibrationSummary
        {
            Total        = population.Analyses.Count,
            ByStatus     = population.Analyses
                .GroupBy(a => a.Status, StringComparer.Ordinal)
                .Select(g => new PersonCalibrationCount { Code = g.Key, Count = g.Count() })
                .OrderByDescending(c => c.Count)
                .ThenBy(c => c.Code, StringComparer.Ordinal)
                .ToList(),
            BySuggestion = Suggestions
                .Select(s => new PersonCalibrationCount { Code = s, Count = population.Cases.Count(c => c.Suggestion == s) })
                .ToList(),
            Decided      = decided,
            Pending      = population.Analyses.Count - decided,
            Pilot        = new PersonCalibrationPilot
            {
                FirstAnalysisAt = milestone.FirstCreatedAt,
                TotalToDate     = milestone.Count,
                Reached20       = milestone.Count >= PilotAnalysesTarget,
                Reached30Days   = milestone.FirstCreatedAt is { } first && (nowUtc - first).TotalDays >= PilotDaysTarget
            }
        };
    }

    // ── decisions ─────────────────────────────────────────────────────────────────

    private async Task<PersonCalibrationDecisions> BuildDecisionsAsync(
        string tenantId, Population population, CancellationToken ct)
    {
        var matrix = Suggestions
            .Select(s =>
            {
                var cases = population.Cases.Where(c => c.Suggestion == s).ToList();
                return new PersonCalibrationMatrixRow
                {
                    Suggestion  = s,
                    Approved    = cases.Count(c => c.Outcome == PersonCalibrationOutcomes.Approved),
                    Rejected    = cases.Count(c => c.Outcome == PersonCalibrationOutcomes.Rejected),
                    PreApproved = cases.Count(c => c.Outcome == PersonCalibrationOutcomes.PreApproved),
                    Pending     = cases.Count(c => c.Outcome == PersonCalibrationOutcomes.Pending),
                    Other       = cases.Count(c => c.Outcome == PersonCalibrationOutcomes.Other)
                };
            })
            .ToList();

        var flagged = population.Cases
            .Where(c => c.IsFlagged)
            .OrderByDescending(c => c.Row.CreatedAt)
            .ThenBy(c => c.Row.AnalysisId)
            .ToList();

        var page = flagged.Take(FlaggedCasesCap).ToList();

        var entities = await EntityNamesAsync(tenantId, page.Select(c => c.Row.EntityId), ct).ConfigureAwait(false);
        var users    = await UsersAsync(tenantId,
            page.SelectMany(c => new[] { (Guid?)c.Row.RequestedBy, DeciderOf(c.Row).By }), ct).ConfigureAwait(false);
        var teams    = page.Count == 0
            ? new Dictionary<Guid, string>()
            : await TeamNamesAsync(tenantId, ct).ConfigureAwait(false);

        return new PersonCalibrationDecisions
        {
            Matrix       = matrix,
            FlaggedCases = new DashboardPage<PersonCalibrationCase>
            {
                TotalCount = flagged.Count,
                Items      = page.Select(c =>
                {
                    entities.TryGetValue(c.Row.EntityId, out var entity);
                    var (decidedBy, decidedAt) = DeciderOf(c.Row);
                    var (teamId, teamName)     = TeamOf(c.Row.RequestedBy, users, teams);

                    return new PersonCalibrationCase
                    {
                        AnalysisId                   = c.Row.AnalysisId,
                        ResultId                     = c.Row.ResultId,
                        EntityId                     = c.Row.EntityId,
                        EntityName                   = entity?.BusinessName,
                        EntityCuit                   = entity?.CUIT,
                        CreatedAt                    = c.Row.CreatedAt,
                        RequestedBy                  = c.Row.RequestedBy,
                        RequestedByName              = ActorName(c.Row.RequestedBy, users),
                        TeamId                       = teamId,
                        TeamName                     = teamName,
                        Status                       = c.Row.Status,
                        Outcome                      = c.Outcome,
                        Suggestion                   = c.Suggestion,
                        DecidedBy                    = decidedBy,
                        DecidedByName                = ActorName(decidedBy, users),
                        DecidedAt                    = decidedAt,
                        Reason                       = ReasonOf(c.Row),
                        RechazoFilterIds             = c.RechazoFilterIds,
                        RojoIndicatorIds             = c.RojoIndicatorIds,
                        CounterGuaranteeRatio        = c.CounterGuarantee?.Ratio,
                        FrozenMinCounterGuaranteePct = c.FrozenMinPct,
                        Currency                     = c.Solicitante.Currency,
                        Flags                        = c.Flags
                    };
                }).ToList()
            }
        };
    }

    // ── filters ───────────────────────────────────────────────────────────────────

    private static PersonCalibrationFilters BuildFilters(Population population)
    {
        var items = population.Cases
            .SelectMany(c => c.Filters)
            .GroupBy(f => f.Id, StringComparer.Ordinal)
            .Select(g =>
            {
                var total    = g.Count();
                var sinDatos = g.Count(f => f.Outcome == PersonFilterOutcomes.SinDatos);
                return new PersonCalibrationFilter
                {
                    FilterId     = g.Key,
                    Ok           = g.Count(f => f.Outcome == PersonFilterOutcomes.Ok),
                    Alerta       = g.Count(f => f.Outcome == PersonFilterOutcomes.Alerta),
                    Rechazo      = g.Count(f => f.Outcome == PersonFilterOutcomes.Rechazo),
                    SinDatos     = sinDatos,
                    Total        = total,
                    SinDatosRate = total == 0 ? 0m : Math.Round((decimal)sinDatos / total, 4, MidpointRounding.AwayFromZero)
                };
            })
            .OrderByDescending(f => f.SinDatos)
            .ThenBy(f => f.FilterId, StringComparer.Ordinal)
            .ToList();

        var cannotConclude = population.Cases
            .SelectMany(c => c.Coverage.CannotConclude ?? [])
            .Where(cc => cc is not null)
            .GroupBy(cc => cc.Code, StringComparer.Ordinal)
            .Select(g => new PersonCalibrationCount { Code = g.Key, Count = g.Count() })
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Code, StringComparer.Ordinal)
            .ToList();

        return new PersonCalibrationFilters { Items = items, CannotConclude = cannotConclude };
    }

    // ── indicators ────────────────────────────────────────────────────────────────

    private static IReadOnlyList<PersonCalibrationIndicator> BuildIndicators(Population population)
        => population.Cases
            .SelectMany(c => c.Indicators.Select(i => (Case: c, Indicator: i)))
            .GroupBy(x => x.Indicator.Id, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new PersonCalibrationIndicator
            {
                IndicatorId = g.Key,
                Bands       = g
                    .GroupBy(x => BandOf(x.Indicator), StringComparer.Ordinal)
                    .OrderBy(b => BandRank(b.Key))
                    .ThenBy(b => b.Key, StringComparer.Ordinal)
                    .Select(b => new PersonCalibrationBand
                    {
                        Band        = b.Key,
                        Count       = b.Count(),
                        Approved    = b.Count(x => x.Case.Outcome == PersonCalibrationOutcomes.Approved),
                        PreApproved = b.Count(x => x.Case.Outcome == PersonCalibrationOutcomes.PreApproved),
                        Rejected    = b.Count(x => x.Case.Outcome == PersonCalibrationOutcomes.Rejected)
                    })
                    .ToList()
            })
            .ToList();

    private static string BandOf(PersonIndicatorResult indicator)
        => indicator.Status == PersonIndicatorStatuses.SinDatos ? SinDatosBand
         : indicator.Band ?? SinBandaBand;

    private static int BandRank(string band)
    {
        var index = Array.IndexOf(BandOrder, band);
        return index < 0 ? BandOrder.Length : index;
    }

    // ── documents ─────────────────────────────────────────────────────────────────

    private static PersonCalibrationDocuments BuildDocuments(Population population)
    {
        var items = population.Cases
            .SelectMany(c => (c.Coverage.Items ?? [])
                .Where(i => i is not null)
                .Select(i => (Regime: c.Coverage.FiscalRegime ?? FiscalRegimes.Desconocido, Item: i)))
            .GroupBy(x => (x.Regime, x.Item.DocumentType))
            .Select(g => new PersonCalibrationDocument
            {
                FiscalRegime = g.Key.Regime,
                DocumentType = g.Key.DocumentType,
                Analyses     = g.Count(),
                Required     = g.Count(x => x.Item.Required),
                Missing      = g.Count(x => IsMissing(x.Item)),
                Stale        = g.Count(x => IsStale(x.Item)),
                SeasonalGap  = g.Count(x => x.Item.SeasonalGap == true)
            })
            .OrderBy(d => d.FiscalRegime, StringComparer.Ordinal)
            .ThenByDescending(d => d.Missing + d.Stale)
            .ThenBy(d => d.DocumentType, StringComparer.Ordinal)
            .ToList();

        var snapshots = population.Cases
            .SelectMany(c => c.Coverage.Snapshots ?? [])
            .Where(s => s is not null)
            .GroupBy(s => s.Source, StringComparer.Ordinal)
            .Select(g => new PersonCalibrationSnapshot
            {
                Source = g.Key,
                Total  = g.Count(),
                Stale  = g.Count(s => s.IsStale)
            })
            .OrderBy(s => s.Source, StringComparer.Ordinal)
            .ToList();

        return new PersonCalibrationDocuments { Items = items, Snapshots = snapshots };
    }

    private static bool IsMissing(PersonCoverageItem item) => item.Required && !item.Present;

    // The engine already clears IsStale on a seasonal gap; the second test only guards a future engine.
    private static bool IsStale(PersonCoverageItem item) => item.IsStale && item.SeasonalGap != true;

    // ── reevaluations ─────────────────────────────────────────────────────────────

    private async Task<PersonCalibrationReevaluations> BuildReevaluationsAsync(
        string tenantId, Population population, CancellationToken ct)
    {
        var rescores = await _repository
            .GetRescoresAsync(tenantId, population.Analyses.Select(a => a.AnalysisId).ToList(), ct)
            .ConfigureAwait(false);

        var recent = rescores.Take(RecentReevaluations).ToList();
        var users  = await UsersAsync(tenantId, recent.Select(r => (Guid?)r.RescoredBy), ct).ConfigureAwait(false);

        // The rescores were read by the population's own analysis ids, so each one's entity is already in hand —
        // from the same tenant-scoped analyses query, with no second read.
        var entityOf = population.Analyses.ToDictionary(a => a.AnalysisId, a => a.EntityId);

        return new PersonCalibrationReevaluations
        {
            AnalysesReevaluated = rescores.Select(r => r.AnalysisId).Distinct().Count(),
            TotalReevaluations  = rescores.Count,
            SuggestionChanges   = rescores.Count(r => !string.Equals(r.PreviousSuggestion, r.NewSuggestion, StringComparison.Ordinal)),
            Flips               = rescores
                .GroupBy(r => (r.PreviousSuggestion, r.NewSuggestion))
                .Select(g => new PersonCalibrationFlip { Previous = g.Key.PreviousSuggestion, New = g.Key.NewSuggestion, Count = g.Count() })
                .OrderByDescending(f => f.Count)
                .ThenBy(f => f.Previous, StringComparer.Ordinal)
                .ThenBy(f => f.New, StringComparer.Ordinal)
                .ToList(),
            Recent              = recent
                .Select(r => new PersonCalibrationReevaluation
                {
                    RescoreId          = r.RescoreId,
                    AnalysisId         = r.AnalysisId,
                    ResultId           = r.ResultId,
                    EntityId           = entityOf.TryGetValue(r.AnalysisId, out var entityId) ? entityId : null,
                    RescoredAt         = r.RescoredAt,
                    RescoredBy         = r.RescoredBy,
                    RescoredByName     = ActorName(r.RescoredBy, users),
                    PreviousSuggestion = r.PreviousSuggestion,
                    NewSuggestion      = r.NewSuggestion
                })
                .ToList()
        };
    }

    // ── parameters ────────────────────────────────────────────────────────────────

    private async Task<PersonCalibrationParameters> BuildParametersAsync(
        string tenantId, Population population, CancellationToken ct)
    {
        var rows = await _repository.GetPersonConfigRowsAsync(tenantId, ct).ConfigureAwait(false);

        var byKey     = rows.GroupBy(r => r.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var values    = byKey.ToDictionary(kv => kv.Key, kv => kv.Value.Value, StringComparer.Ordinal);
        var overrides = PersonEvaluationParameters.From(values).TenantOverrides.ToHashSet(StringComparer.Ordinal);

        var items = PersonEvaluationDefaults.Layout
            .Select(entry =>
            {
                var overridden = overrides.Contains(entry.Key);
                byKey.TryGetValue(entry.Key, out var row);
                return new PersonCalibrationParameter
                {
                    Key          = entry.Key,
                    Group        = entry.Group,
                    Unit         = entry.Unit,
                    DefaultValue = PersonEvaluationDefaults.Get(entry.Key),
                    CurrentValue = overridden && row is not null ? row.Value : PersonEvaluationDefaults.Get(entry.Key),
                    Overridden   = overridden,
                    UpdatedAt    = overridden ? row?.UpdatedAt : null
                };
            })
            .ToList();

        var versions = population.Cases
            .GroupBy(c => c.Solicitante.EvaluationVersion, StringComparer.Ordinal)
            .Select(g => new PersonCalibrationCount { Code = g.Key, Count = g.Count() })
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Code, EngineVersion.Comparer)     // "0.9" before "0.10": dotted numbers, not text
            .ToList();

        return new PersonCalibrationParameters
        {
            Items              = items,
            EvaluationVersions = versions,
            FrozenDifferences  = FrozenDifferences(population.Cases)
        };
    }

    /// <summary>
    /// The frozen parameters whose value differs across <paramref name="cases"/>. Each case's frozen
    /// <see cref="PersonEvaluationParameters"/> — read by the single reader — is written back with the same
    /// options and compared member by member, so a member this code has never heard of is compared too.
    /// TenantOverrides is the list of overridden KEYS, not a parameter, and is left out.
    /// </summary>
    private static IReadOnlyList<PersonCalibrationFrozenDifference> FrozenDifferences(IReadOnlyList<CalibrationCase> cases)
    {
        const string tenantOverrides = "tenantOverrides";

        var frozen = cases
            .Where(c => c.Sources.Parameters is not null)
            .Select(c => JsonSerializer.SerializeToElement(c.Sources.Parameters, PersonEvaluationJson.Options))
            .ToList();

        var names = frozen
            .SelectMany(e => e.EnumerateObject().Select(p => p.Name))
            .Where(n => !string.Equals(n, tenantOverrides, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var differences = new List<PersonCalibrationFrozenDifference>();
        foreach (var name in names)
        {
            var values = frozen
                .Select(e => e.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
                    ? value.GetRawText()
                    : null)
                .GroupBy(v => v, StringComparer.Ordinal)
                .Select(g => new PersonCalibrationFrozenValue { Value = g.Key, Count = g.Count() })
                .OrderByDescending(v => v.Count)
                .ThenBy(v => v.Value, StringComparer.Ordinal)
                .ToList();

            if (values.Count > 1)
                differences.Add(new PersonCalibrationFrozenDifference { Parameter = name, Values = values });
        }

        return differences;
    }
}
