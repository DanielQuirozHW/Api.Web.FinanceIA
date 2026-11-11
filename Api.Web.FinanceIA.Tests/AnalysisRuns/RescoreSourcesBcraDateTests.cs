// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Xunit;

namespace Api.Web.FinanceIA.Tests.AnalysisRuns;

/// <summary>
/// Score credibility 1b-1, V13: the BCRA entry of a score's sources says when the Central de Deudores
/// answer was actually FETCHED (BcraService caches it for 24 h), and is live only when that answer was
/// real. Pure functions, no world needed.
/// </summary>
public sealed class RescoreSourcesBcraDateTests
{
    private static readonly DateTime Now       = new(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FetchedAt = new(2026, 11, 1, 19, 30, 0, DateTimeKind.Utc);

    private static EnrichmentSnapshot Snapshot(BcraData? bcra, BcraEnrichmentOutcome outcome = BcraEnrichmentOutcome.Complete)
        => new(
            new Entity { EntityId = Guid.NewGuid(), TenantId = "t", CUIT = "30-12345678-9" },
            bcra, null, null,
            new BcraEnrichmentAssessment(outcome, null));

    private static RescoreSource Bcra(IReadOnlyList<RescoreSource> sources)
        => Assert.Single(sources, s => s.Source == RescoreSource.Bcra);

    [Theory]
    [InlineData(BcraDeudaConsultaEstado.DebtFound)]
    [InlineData(BcraDeudaConsultaEstado.NoDebtReported)]
    public void FromEnrichment_RealDeuda_IsLive_AndDatedByItsFetchedAt(BcraDeudaConsultaEstado estado)
    {
        var bcra = new BcraData { Deuda = new BcraDeudaData { Estado = estado, FetchedAt = FetchedAt } };

        var source = Bcra(RescoreSources.FromEnrichment(Snapshot(bcra), Now));

        Assert.True(source.Live);
        Assert.Equal(FetchedAt, source.AsOf);
    }

    [Fact]
    public void FromEnrichment_DeudaWithoutAStamp_FallsBackToNow()
    {
        var bcra = new BcraData { Deuda = new BcraDeudaData { Estado = BcraDeudaConsultaEstado.DebtFound } };

        var source = Bcra(RescoreSources.FromEnrichment(Snapshot(bcra), Now));

        Assert.True(source.Live);
        Assert.Equal(Now, source.AsOf);
    }

    [Fact]
    public void FromEnrichment_DeudaLookupFailed_IsNotLive_AndHasNoDate_EvenWithMacro()
    {
        var bcra = new BcraData
        {
            Deuda = new BcraDeudaData { Estado = BcraDeudaConsultaEstado.LookupFailed, FetchedAt = FetchedAt },
            Macro = new BcraMacroData { InflacionMensual = 2.0 }
        };

        var source = Bcra(RescoreSources.FromEnrichment(Snapshot(bcra, BcraEnrichmentOutcome.Degraded), Now));

        Assert.False(source.Live);
        Assert.Null(source.AsOf);
    }

    [Fact]
    public void FromEnrichment_NoPayload_IsNotLive()
    {
        var source = Bcra(RescoreSources.FromEnrichment(Snapshot(null, BcraEnrichmentOutcome.NotApplicable), Now));

        Assert.False(source.Live);
        Assert.Null(source.AsOf);
    }

    [Fact]
    public void ForCurrentResult_Derived_UsesTheDeudaFetchDate_WhenKnown()
    {
        var generatedAt = Now;

        var withDate    = Bcra(RescoreSources.ForCurrentResult(null, generatedAt, bcraAnswered: true, entity: null, FetchedAt));
        var withoutDate = Bcra(RescoreSources.ForCurrentResult(null, generatedAt, bcraAnswered: true, entity: null, null));
        var notAnswered = Bcra(RescoreSources.ForCurrentResult(null, generatedAt, bcraAnswered: false, entity: null, FetchedAt));

        Assert.Equal(FetchedAt, withDate.AsOf);
        Assert.Equal(generatedAt, withoutDate.AsOf);
        Assert.Null(notAnswered.AsOf);
        Assert.False(notAnswered.Live);
    }

    [Fact]
    public void ForCurrentResult_StoredSourcesJson_IsReturnedAsIs()
    {
        var stored = """[{"source":"BCRA","asOf":"2026-10-01T00:00:00Z","live":true}]""";

        var source = Bcra(RescoreSources.ForCurrentResult(stored, Now, bcraAnswered: true, entity: null, FetchedAt));

        Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), source.AsOf!.Value.ToUniversalTime());
    }

    [Theory]
    [InlineData("2026-11-01T19:30:00Z")]
    [InlineData("2026-11-01T19:30:00.0000000Z")]
    [InlineData("2026-11-01T16:30:00-03:00")]
    public void ParseFetchedAt_ReadsRoundTripText_AsUtc(string raw)
    {
        var parsed = RescoreSources.ParseFetchedAt(raw);

        Assert.Equal(FetchedAt, parsed);
        Assert.Equal(DateTimeKind.Utc, parsed!.Value.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    [InlineData("0001-01-01T00:00:00")]
    public void ParseFetchedAt_DiscardsMissingUnreadableAndTheYearOneDefault(string? raw)
        => Assert.Null(RescoreSources.ParseFetchedAt(raw));
}
