// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-06

using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.AnalysisComparison;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Codes = Api.Web.FinanceIA.Models.Financia.AnalysisComparisonCodes;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The availability branches of <see cref="AnalysisComparisonService"/>, over a hand-rolled repository.
/// </summary>
public sealed class AnalysisComparisonServiceTests
{
    private const string Tenant = "tenant-1";

    private static AnalysisComparisonSnapshot Snapshot(Action<AnalysisComparisonSnapshot>? configure = null)
    {
        var snapshot = new AnalysisComparisonSnapshot
        {
            ResultId         = Guid.NewGuid(),
            AnalysisId       = Guid.NewGuid(),
            EntityId         = Guid.NewGuid(),
            AnalysisKind     = AnalysisKinds.Balance,
            Status           = AnalysisStatuses.Approved,
            FiscalYear       = 2025,
            PromptVersion    = "2.20",
            CreatedAt        = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            AiSuggestedScore = 30m,
            AiSuggestedLevel = "Medium",
        };
        configure?.Invoke(snapshot);
        return snapshot;
    }

    private sealed class StubRepository : IAnalysisComparisonRepository
    {
        public AnalysisComparisonSnapshot? Current { get; init; }

        public AnalysisComparisonSnapshot? Previous { get; init; }

        public (string TenantId, Guid EntityId, string Kind, Guid AnalysisId, DateTime CreatedAt)? PreviousQuery { get; private set; }

        public string? SnapshotTenant { get; private set; }

        public Task<AnalysisComparisonSnapshot?> GetSnapshotByResultIdAsync(Guid resultId, string tenantId, CancellationToken ct)
        {
            SnapshotTenant = tenantId;
            return Task.FromResult(Current is not null && Current.ResultId == resultId ? Current : null);
        }

        public Task<AnalysisComparisonSnapshot?> GetPreviousSnapshotAsync(
            string tenantId, Guid entityId, string analysisKind, Guid currentAnalysisId, DateTime currentCreatedAt, CancellationToken ct)
        {
            PreviousQuery = (tenantId, entityId, analysisKind, currentAnalysisId, currentCreatedAt);
            return Task.FromResult(Previous);
        }

        /// <summary>The persona facts by AnalysisId; an analysis absent here has no stored evaluation.</summary>
        public Dictionary<Guid, PersonComparisonFacts> PersonFacts { get; } = [];

        public List<(Guid AnalysisId, string TenantId)> PersonFactsQueries { get; } = [];

        public Task<PersonComparisonFacts?> GetPersonFactsAsync(Guid analysisId, string tenantId, CancellationToken ct)
        {
            PersonFactsQueries.Add((analysisId, tenantId));
            return Task.FromResult(PersonFacts.TryGetValue(analysisId, out var facts) ? facts : null);
        }
    }

    private static AnalysisComparisonSnapshot PersonaSnapshot(Action<AnalysisComparisonSnapshot>? configure = null)
        => Snapshot(s =>
        {
            s.AnalysisKind     = AnalysisKinds.PersonaHumana;
            s.PromptVersion    = "pf-0.10";
            s.AiSuggestedScore = null;
            s.AiSuggestedLevel = null;
            configure?.Invoke(s);
        });

    private static PersonComparisonFacts PersonFacts(string currency = "ARS", decimal amount = 1_000_000m) => new()
    {
        FiltersJson       = """[{"id":"bcra_situacion","outcome":"Ok","evidence":"e","source":"s","inputs":{"situacionActual":"1"}}]""",
        IndicatorsJson    = """[{"id":"cuota_ingreso","status":"Ok","value":0.25,"unit":"ratio","evidence":"e"}]""",
        CoverageJson      = "{}",
        EvaluationVersion = "0.10",
        AmountAtCharge    = amount,
        Currency          = currency,
        InstallmentAmount = 50_000m,
    };

    private static AnalysisComparisonService Sut(StubRepository repository)
        => new(repository, NullLogger<AnalysisComparisonService>.Instance);

    [Fact]
    public async Task A_result_outside_the_tenant_is_null()
    {
        var repository = new StubRepository { Current = Snapshot() };

        Assert.Null(await Sut(repository).CompareWithPreviousAsync(Guid.NewGuid(), Tenant, CancellationToken.None));
        Assert.Equal(Tenant, repository.SnapshotTenant);
        Assert.Null(repository.PreviousQuery);
    }

    [Fact]
    public async Task Another_kind_is_unsupported_and_never_looks_for_a_previous()
    {
        var current    = Snapshot(s => s.AnalysisKind = "Otro");
        var repository = new StubRepository { Current = current, Previous = Snapshot() };

        var response = await Sut(repository).CompareWithPreviousAsync(current.ResultId, Tenant, CancellationToken.None);

        Assert.False(response!.Available);
        Assert.Equal(Codes.Reasons.UnsupportedKind, response.Reason);
        Assert.Null(response.Groups);
        Assert.Null(repository.PreviousQuery);
        Assert.Empty(repository.PersonFactsQueries);
    }

    [Fact]
    public async Task A_persona_analysis_without_a_stored_evaluation_has_no_result_and_never_looks_for_a_previous()
    {
        var current    = PersonaSnapshot();
        var repository = new StubRepository { Current = current, Previous = PersonaSnapshot() };

        var response = await Sut(repository).CompareWithPreviousAsync(current.ResultId, Tenant, CancellationToken.None);

        Assert.False(response!.Available);
        Assert.Equal(Codes.Reasons.NoResult, response.Reason);
        Assert.Null(repository.PreviousQuery);
        Assert.Equal([(current.AnalysisId, Tenant)], repository.PersonFactsQueries);
    }

    [Fact]
    public async Task A_persona_analysis_with_no_previous_is_not_available()
    {
        var current    = PersonaSnapshot();
        var repository = new StubRepository { Current = current };
        repository.PersonFacts[current.AnalysisId] = PersonFacts();

        var response = await Sut(repository).CompareWithPreviousAsync(current.ResultId, Tenant, CancellationToken.None);

        Assert.False(response!.Available);
        Assert.Equal(Codes.Reasons.NoPrevious, response.Reason);
        Assert.Equal((Tenant, current.EntityId, AnalysisKinds.PersonaHumana, current.AnalysisId, current.CreatedAt), repository.PreviousQuery);
    }

    [Fact]
    public async Task A_persona_previous_whose_evaluation_is_gone_reads_as_no_previous()
    {
        var current    = PersonaSnapshot();
        var previous   = PersonaSnapshot();
        var repository = new StubRepository { Current = current, Previous = previous };
        repository.PersonFacts[current.AnalysisId] = PersonFacts();

        var response = await Sut(repository).CompareWithPreviousAsync(current.ResultId, Tenant, CancellationToken.None);

        Assert.False(response!.Available);
        Assert.Equal(Codes.Reasons.NoPrevious, response.Reason);
    }

    [Fact]
    public async Task A_persona_analysis_is_compared_with_its_previous_evaluation()
    {
        var current    = PersonaSnapshot();
        var previous   = PersonaSnapshot();
        var repository = new StubRepository { Current = current, Previous = previous };
        repository.PersonFacts[current.AnalysisId]  = PersonFacts(amount: 1_200_000m);
        repository.PersonFacts[previous.AnalysisId] = PersonFacts();

        var response = await Sut(repository).CompareWithPreviousAsync(current.ResultId, Tenant, CancellationToken.None);

        Assert.True(response!.Available);
        Assert.Equal([(current.AnalysisId, Tenant), (previous.AnalysisId, Tenant)], repository.PersonFactsQueries);
        Assert.Null(response.SameFiscalYear);
        Assert.Equal("ARS", response.Current!.Currency);
        Assert.Null(response.Current.Score);
        Assert.Equal(
            [Codes.Groups.Suggestion, Codes.Groups.Filters, Codes.Groups.Indicators, Codes.Groups.Bcra, Codes.Groups.Terms],
            response.Groups!.Select(g => g.Key));
        Assert.Contains(Codes.Notes.TermsChanged, response.Notes!);
    }

    [Fact]
    public async Task A_malformed_persona_blob_never_throws()
    {
        var current    = PersonaSnapshot();
        var previous   = PersonaSnapshot();
        var repository = new StubRepository { Current = current, Previous = previous };
        var broken     = PersonFacts();
        broken.FiltersJson          = "{";
        broken.IndicatorsJson       = "[1,";
        broken.CoverageJson         = "[]x";
        broken.DocumentVersionsJson = "nope";
        repository.PersonFacts[current.AnalysisId]  = broken;
        repository.PersonFacts[previous.AnalysisId] = PersonFacts();

        var response = await Sut(repository).CompareWithPreviousAsync(current.ResultId, Tenant, CancellationToken.None);

        Assert.True(response!.Available);
        Assert.Equal([Codes.Groups.Terms], response.Groups!.Select(g => g.Key));
    }

    [Fact]
    public async Task An_unscored_analysis_has_no_result_to_compare()
    {
        var current    = Snapshot(s => s.AiSuggestedScore = null);
        var repository = new StubRepository { Current = current, Previous = Snapshot() };

        var response = await Sut(repository).CompareWithPreviousAsync(current.ResultId, Tenant, CancellationToken.None);

        Assert.False(response!.Available);
        Assert.Equal(Codes.Reasons.NoResult, response.Reason);
    }

    [Fact]
    public async Task No_previous_analysis_is_not_available()
    {
        var current    = Snapshot();
        var repository = new StubRepository { Current = current };

        var response = await Sut(repository).CompareWithPreviousAsync(current.ResultId, Tenant, CancellationToken.None);

        Assert.False(response!.Available);
        Assert.Equal(Codes.Reasons.NoPrevious, response.Reason);
        Assert.Null(response.Current);
        Assert.Null(response.Previous);
        Assert.Null(response.Notes);
    }

    [Fact]
    public async Task The_previous_is_looked_up_for_the_same_tenant_entity_and_kind_before_the_current()
    {
        var current    = Snapshot();
        var previous   = Snapshot(s => { s.FiscalYear = 2024; s.AiSuggestedScore = 40m; });
        var repository = new StubRepository { Current = current, Previous = previous };

        var response = await Sut(repository).CompareWithPreviousAsync(current.ResultId, Tenant, CancellationToken.None);

        Assert.Equal((Tenant, current.EntityId, AnalysisKinds.Balance, current.AnalysisId, current.CreatedAt), repository.PreviousQuery);
        Assert.True(response!.Available);
        Assert.Equal(current.ResultId, response.Current!.ResultId);
        Assert.Equal(previous.ResultId, response.Previous!.ResultId);
        Assert.False(response.SameFiscalYear);
        var score = Assert.Single(response.Groups!.Single(g => g.Key == Codes.Groups.Score).Rows, r => r.Key == "score");
        Assert.Equal(Codes.Trends.Better, score.Trend);
    }

    [Fact]
    public async Task A_malformed_blob_never_throws()
    {
        var current    = Snapshot(s => { s.RatiosJson = "{"; s.BalanceJson = "["; s.ScoreBreakdownJson = "nope"; s.BcraDeudaEstadoRaw = "x"; });
        var repository = new StubRepository { Current = current, Previous = Snapshot() };

        var response = await Sut(repository).CompareWithPreviousAsync(current.ResultId, Tenant, CancellationToken.None);

        Assert.True(response!.Available);
        Assert.Equal([Codes.Groups.Score], response.Groups!.Select(g => g.Key));
    }
}
