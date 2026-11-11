// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-30

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using Microsoft.Extensions.Logging.Abstractions;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// The calibration board's seeding on top of <see cref="DashboardFixture"/>: persona analyses and their frozen
/// participant blobs, written with <see cref="PersonEvaluationJson.Serialize{T}"/> from the REAL result records,
/// so the shapes under test are the shapes the engine freezes. Real repositories, nothing stubbed.
/// </summary>
internal sealed class PersonCalibrationFixture : IAsyncDisposable
{
    /// <summary>October 2026 in Argentina, up to the dashboard fixture's "now" (2026-10-23).</summary>
    public static readonly PersonCalibrationPeriod October =
        PersonCalibrationPeriod.Resolve("2026-10-01", "2026-10-23", DashboardFixture.Now).Period!.Value;

    private PersonCalibrationFixture(DashboardFixture dashboard)
    {
        Dashboard = dashboard;
    }

    public DashboardFixture Dashboard { get; }

    public static PersonCalibrationFixture Create(string name) => new(DashboardFixture.Create("calibration-" + name));

    public ValueTask DisposeAsync() => Dashboard.DisposeAsync();

    public Task SaveAsync() => Dashboard.SaveAsync();

    // ── Seeding ───────────────────────────────────────────────────────────────

    /// <summary>A PersonaHumana analysis, created on <paramref name="createdAtUtc"/> (default: 2026-10-10 15:00 UTC).</summary>
    public (Analysis Analysis, AnalysisResult Result) AddPersonAnalysis(
        Guid requestedBy,
        string status = AnalysisStatuses.PendingReview,
        DateTime? createdAtUtc = null,
        Guid? entityId = null,
        Action<Analysis>? analysis = null,
        string tenant = DashboardFixture.Tenant)
        => Dashboard.AddAnalysis(requestedBy, entityId, a =>
        {
            a.AnalysisKind = AnalysisKinds.PersonaHumana;
            a.Status       = status;
            a.CreatedAt    = createdAtUtc ?? new DateTime(2026, 10, 10, 15, 0, 0, DateTimeKind.Utc);
            analysis?.Invoke(a);
        }, tenant: tenant);

    /// <summary>The solicitante row, its four frozen columns built from real records.</summary>
    public AnalysisParticipant AddSolicitante(
        Guid analysisId,
        IEnumerable<PersonFilterResult>? filters = null,
        IEnumerable<PersonIndicatorResult>? indicators = null,
        PersonCoverageResult? coverage = null,
        PersonEvaluationParameters? parameters = null,
        string currency = "ARS",
        decimal amount = 1_000_000m,
        string version = "0.7",
        string tenant = DashboardFixture.Tenant)
    {
        var participant = new AnalysisParticipant
        {
            ParticipantId        = Guid.NewGuid(),
            TenantId             = tenant,
            AnalysisId           = analysisId,
            PersonLegajoId       = Guid.NewGuid(),
            Role                 = ParticipantRoles.Solicitante,
            AmountAtCharge       = amount,
            Currency             = currency,
            FiltersJson          = PersonEvaluationJson.Serialize((filters ?? [Filter("bcra_situacion", PersonFilterOutcomes.Ok)]).ToList()),
            IndicatorsJson       = PersonEvaluationJson.Serialize((indicators ?? []).ToList()),
            CoverageJson         = PersonEvaluationJson.Serialize(coverage ?? Coverage()),
            DocumentVersionsJson = PersonEvaluationJson.Serialize(Sources(parameters ?? Parameters())),
            EvaluationVersion    = version,
            CreatedAt            = DashboardFixture.Now.AddDays(-3)
        };
        Dashboard.Context.AnalysisParticipants.Add(participant);
        return participant;
    }

    /// <summary>
    /// A fiador row whose frozen coverage computes one executable asset worth <paramref name="executable"/>
    /// and no liabilities: its contribution is min(executable, <paramref name="amountAtCharge"/>).
    /// </summary>
    public AnalysisParticipant AddFiador(
        Guid analysisId, decimal executable, decimal amountAtCharge, string currency = "ARS",
        string tenant = DashboardFixture.Tenant)
    {
        var participant = new AnalysisParticipant
        {
            ParticipantId        = Guid.NewGuid(),
            TenantId             = tenant,
            AnalysisId           = analysisId,
            PersonLegajoId       = Guid.NewGuid(),
            Role                 = ParticipantRoles.Fiador,
            FiadorId             = Guid.NewGuid(),
            AmountAtCharge       = amountAtCharge,
            Currency             = currency,
            FiltersJson          = PersonEvaluationJson.Serialize(new List<PersonFilterResult> { Filter("bcra_situacion", PersonFilterOutcomes.Ok) }),
            IndicatorsJson       = "[]",
            CoverageJson         = PersonEvaluationJson.Serialize(new FiadorCoverageResult
            {
                OperationCurrency     = currency,
                Comparable            = true,
                AmountAtCharge        = amountAtCharge,
                Assets                =
                [
                    new FiadorAssetResult
                    {
                        ItemId          = Guid.NewGuid(),
                        Origin          = "manifestacion",
                        Kind            = "inmueble",
                        MatchKeyKind    = "none",
                        SoloValorFiscal = false,
                        IsHousing       = false,
                        ExecutableValue = executable,
                        Treatment       = FiadorAssetTreatments.Computado,
                        Reason          = "ok",
                        ReasonText      = "ok",
                        EvidenceKind    = PersonEvidenceKinds.Declarado
                    }
                ],
                GrossExecutable       = executable,
                Liabilities           = 0m,
                LiabilitiesSource     = "ninguno",
                GuaranteesGivenAmount = 0m,
                ExecutablePatrimony   = executable,
                CoberturaRojo         = 0.5m,
                CoberturaVerde        = 1m
            }),
            DocumentVersionsJson = "{}",
            EvaluationVersion    = "0.7",
            CreatedAt            = DashboardFixture.Now.AddDays(-3)
        };
        Dashboard.Context.AnalysisParticipants.Add(participant);
        return participant;
    }

    /// <summary>One re-evaluation whose PreviousValuesJson carries the persona suggestion pair.</summary>
    public AnalysisRescore AddRescore(AnalysisResult result, Guid rescoredBy, DateTime rescoredAt, string previous, string @new)
    {
        var rescore = Dashboard.AddRescore(result, rescoredBy, rescoredAt);
        rescore.PreviousValuesJson = $$$"""{"kind":"PersonaHumana","suggestions":{"previous":"{{{previous}}}","new":"{{{@new}}}"}}""";
        return rescore;
    }

    // ── Builders ──────────────────────────────────────────────────────────────

    public static PersonFilterResult Filter(string id, string outcome) => new()
    {
        Id       = id,
        Outcome  = outcome,
        Evidence = "e",
        Source   = "s"
    };

    public static PersonIndicatorResult Indicator(string id, string? band, string status = PersonIndicatorStatuses.Ok) => new()
    {
        Id       = id,
        Status   = status,
        Band     = band,
        Unit     = "ratio",
        Evidence = "e"
    };

    public static PersonCoverageItem Item(
        string documentType, bool required = true, bool present = true, bool isStale = false, bool? seasonalGap = null) => new()
    {
        DocumentType = documentType,
        Required     = required,
        Present      = present,
        IsStale      = isStale,
        SeasonalGap  = seasonalGap
    };

    public static PersonCoverageSnapshot Snapshot(string source, bool isStale) => new()
    {
        Source     = source,
        MaxAgeDays = 30,
        IsStale    = isStale
    };

    public static PersonCoverageResult Coverage(
        string regime = FiscalRegimes.Monotributo,
        IReadOnlyList<PersonCoverageItem>? items = null,
        IReadOnlyList<PersonCoverageSnapshot>? snapshots = null,
        IReadOnlyList<PersonCannotConclude>? cannotConclude = null) => new()
    {
        FiscalRegime    = regime,
        Items           = items ?? [],
        RequiredPresent = 0,
        RequiredTotal   = 0,
        Index           = 1m,
        Snapshots       = snapshots ?? [],
        CannotConclude  = cannotConclude ?? []
    };

    /// <summary>The code defaults with the counter-guarantee minimum frozen at <paramref name="minPct"/>.</summary>
    public static PersonEvaluationParameters Parameters(int? minPct = 60)
        => PersonEvaluationParameters.Defaults with { ContragarantiaCoberturaMinPct = minPct };

    private static PersonEvaluationSources Sources(PersonEvaluationParameters parameters) => new()
    {
        Documents  = [],
        Parameters = parameters
    };

    // ── Real collaborators ────────────────────────────────────────────────────

    public PersonCalibrationRepository Repository() => new(Dashboard.Context);

    public PersonCalibrationService Service(IPersonCalibrationRepository? repository = null) => new(
        repository ?? Repository(),
        new EntityRepository(Dashboard.Context, NullLogger<EntityRepository>.Instance),
        new UserRepository(Dashboard.Context, NullLogger<UserRepository>.Instance),
        new TeamRepository(Dashboard.Context, NullLogger<TeamRepository>.Instance),
        NullLogger<PersonCalibrationService>.Instance);
}

/// <summary>
/// Wraps the real <see cref="IPersonCalibrationRepository"/> and throws from the named methods — the
/// FailingDashboardRepository pattern.
/// </summary>
internal sealed class FailingPersonCalibrationRepository(IPersonCalibrationRepository inner, params string[] failing)
    : IPersonCalibrationRepository
{
    private void ThrowIf(string method)
    {
        if (failing.Contains(method))
            throw new InvalidOperationException("simulated section failure");
    }

    public Task<IReadOnlyList<PersonCalibrationAnalysisRow>> GetAnalysesAsync(
        string tenantId, DateTime startUtc, DateTime endUtc, Guid? teamId, CancellationToken ct)
    {
        ThrowIf(nameof(GetAnalysesAsync));
        return inner.GetAnalysesAsync(tenantId, startUtc, endUtc, teamId, ct);
    }

    public Task<IReadOnlyList<AnalysisParticipant>> GetParticipantsAsync(
        string tenantId, IReadOnlyCollection<Guid> analysisIds, CancellationToken ct)
    {
        ThrowIf(nameof(GetParticipantsAsync));
        return inner.GetParticipantsAsync(tenantId, analysisIds, ct);
    }

    public Task<PersonCalibrationMilestoneRow> GetPilotMilestoneAsync(string tenantId, CancellationToken ct)
    {
        ThrowIf(nameof(GetPilotMilestoneAsync));
        return inner.GetPilotMilestoneAsync(tenantId, ct);
    }

    public Task<IReadOnlyList<PersonCalibrationRescoreRow>> GetRescoresAsync(
        string tenantId, IReadOnlyCollection<Guid> analysisIds, CancellationToken ct)
    {
        ThrowIf(nameof(GetRescoresAsync));
        return inner.GetRescoresAsync(tenantId, analysisIds, ct);
    }

    public Task<IReadOnlyList<PersonCalibrationConfigRow>> GetPersonConfigRowsAsync(string tenantId, CancellationToken ct)
    {
        ThrowIf(nameof(GetPersonConfigRowsAsync));
        return inner.GetPersonConfigRowsAsync(tenantId, ct);
    }
}
