// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-25

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// The portfolio's risk blocks over real repositories on one in-memory database: bcraCurrent (the current
/// BCRA situación per currency, by the shared reading), annualReview (PYME_REVIEW_DUE's rule and query) and
/// sgr (the Fondo de Riesgo, by CreditLineRules.EffectiveStatus). This file: bcraCurrent and the isolation of
/// the three sections.
/// </summary>
public sealed partial class DashboardServicePortfolioRiskTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTime Now = DashboardFixture.Now;   // 23/10/2026 12:00 UTC = 09:00 in Buenos Aires
    private const string Tenant = DashboardFixture.Tenant;
    private const string Other  = DashboardFixture.Other;

    private static Task<PortfolioResponse> PortfolioAsync(DashboardFixture f, DateTime? now = null, IDashboardRepository? repository = null)
        => f.Service(repository).GetPortfolioAsync(Tenant, now ?? Now, Ct);

    private static string Debt(int situacion) => DashboardFixture.BcraJson(BcraDeudaConsultaEstado.DebtFound, situacion);

    private static readonly string NoDebt = DashboardFixture.BcraJson(BcraDeudaConsultaEstado.NoDebtReported, null);
    private static readonly string Failed = DashboardFixture.BcraJson(BcraDeudaConsultaEstado.LookupFailed, null);

    private static Entity WithBcra(Entity entity, string? json, DateTime? updatedAt)
    {
        entity.BcraDataJson      = json;
        entity.BcraDataUpdatedAt = updatedAt;
        return entity;
    }

    // ── bcraCurrent ───────────────────────────────────────────────────────────

    [Fact]
    public async Task BcraCurrent_BucketsEntitiesByTheSharedReading_PerCurrency_InOrder()
    {
        await using var f = DashboardFixture.Create(nameof(BcraCurrent_BucketsEntitiesByTheSharedReading_PerCurrency_InOrder));
        var both    = WithBcra(f.AddEntity(), NoDebt, Now.AddDays(-1));                 // 1, in ARS and USD
        var sit3    = WithBcra(f.AddEntity(), Debt(3), Now.AddDays(-2));
        var sit6    = WithBcra(f.AddEntity(), Debt(6), Now.AddDays(-2));
        var failed  = WithBcra(f.AddEntity(), Failed, Now.AddDays(-3));                 // null: lookup failed
        var never   = WithBcra(f.AddEntity(), null, null);                              // null: never queried
        var noMax   = WithBcra(f.AddEntity(), DashboardFixture.BcraJson(BcraDeudaConsultaEstado.DebtFound, null), Now.AddDays(-1)); // null
        var noDeuda = WithBcra(f.AddEntity(), """{"macro":null}""", Now.AddDays(-1));    // null: no Deuda block

        f.AddOperation(f.AddCreditLine(both.EntityId, "ARS", 1_000m), 300m);
        f.AddOperation(f.AddCreditLine(both.EntityId, "USD", 10m), 4m);
        f.AddOperation(f.AddCreditLine(sit3.EntityId, "ARS", 500m), 100m);
        f.AddOperation(f.AddCreditLine(sit3.EntityId, "ARS", 500m), 999m, status: CreditLineRules.Cancelada);
        f.AddCreditLine(sit6.EntityId, "ARS", 200m);
        f.AddCreditLine(failed.EntityId, "ARS", 100m);
        f.AddCreditLine(never.EntityId, "ARS", 50m);
        f.AddCreditLine(noMax.EntityId, "ARS", 25m);
        f.AddCreditLine(noDeuda.EntityId, "ARS", 5m);
        f.AddCreditLine(sit3.EntityId, "ARS", 7_000m, status: "Suspended");             // not Active: not counted
        await f.SaveAsync();

        var response = await PortfolioAsync(f);

        Assert.Empty(response.DegradedSections);
        Assert.Equal(["ARS", "USD"], response.BcraCurrent!.Select(c => c.Currency));

        var ars = response.BcraCurrent![0];
        Assert.Equal(
            [((int?)1, 1, 1_000m, 300m), (3, 1, 1_000m, 100m), (6, 1, 200m, 0m), (null, 4, 180m, 0m)],
            ars.Buckets.Select(b => (b.Situacion, b.EntityCount, b.Otorgado, b.Utilizado)));

        var usd = Assert.Single(Assert.Single(response.BcraCurrent!, c => c.Currency == "USD").Buckets);
        Assert.Equal(((int?)1, 1, 10m, 4m), (usd.Situacion, usd.EntityCount, usd.Otorgado, usd.Utilizado));

        // The buckets add up to the totals of the same currency: no money is lost to an unreadable snapshot.
        foreach (var currency in response.BcraCurrent!)
        {
            var totals = Assert.Single(response.Totals!, t => t.Currency == currency.Currency);
            Assert.Equal(totals.Otorgado, currency.Buckets.Sum(b => b.Otorgado));
            Assert.Equal(totals.Utilizado, currency.Buckets.Sum(b => b.Utilizado));
        }
    }

    [Fact]
    public async Task BcraCurrent_TheNowSide_AgreesWithBcraSignalRule()
    {
        // Every raw pair the shared reading distinguishes, through the section and through the rule.
        await using var f = DashboardFixture.Create(nameof(BcraCurrent_TheNowSide_AgreesWithBcraSignalRule));
        var cases = new[] { NoDebt, Debt(1), Debt(2), Debt(4), Debt(5), Failed, null };
        foreach (var json in cases)
            f.AddCreditLine(WithBcra(f.AddEntity(), json, Now.AddDays(-1)).EntityId);
        await f.SaveAsync();

        var buckets = Assert.Single((await PortfolioAsync(f)).BcraCurrent!).Buckets;

        var expected = cases
            .Select(json => BcraSignalRule.Situacion(
                SqlJsonFunctionsValue(json, "$.deuda.estado"), SqlJsonFunctionsValue(json, "$.deuda.situacionMaxima")))
            .GroupBy(s => s)
            .OrderBy(g => g.Key is null).ThenBy(g => g.Key)
            .Select(g => (g.Key, g.Count()));

        Assert.Equal(expected, buckets.Select(b => (b.Situacion, b.EntityCount)));
        Assert.Equal([(int?)1, 2, 4, 5, null], buckets.Select(b => b.Situacion));
    }

    private static string? SqlJsonFunctionsValue(string? json, string path) => Api.Web.FinanceIA.Data.SqlJsonFunctions.JsonValue(json, path);

    [Fact]
    public async Task BcraCurrent_StaleIsMoreThanThirtyArgentineDays_NeverQueriedApart_OldestIsTheMinimum()
    {
        await using var f = DashboardFixture.Create(nameof(BcraCurrent_StaleIsMoreThanThirtyArgentineDays_NeverQueriedApart_OldestIsTheMinimum));
        // Today in Buenos Aires is 23/10/2026.
        var day30      = new DateTime(2026, 9, 23, 15, 0, 0, DateTimeKind.Utc);   // 23/09 AR: 30 days, not stale
        var day31      = new DateTime(2026, 9, 22, 15, 0, 0, DateTimeKind.Utc);   // 22/09 AR: 31 days, stale
        var lateNight  = new DateTime(2026, 9, 23, 2, 0, 0, DateTimeKind.Utc);    // 22/09 23:00 AR: 31 days, stale (UTC says 30)
        f.AddCreditLine(WithBcra(f.AddEntity(), NoDebt, day30).EntityId);
        f.AddCreditLine(WithBcra(f.AddEntity(), NoDebt, day31).EntityId);
        f.AddCreditLine(WithBcra(f.AddEntity(), NoDebt, lateNight).EntityId);
        f.AddCreditLine(WithBcra(f.AddEntity(), null, null).EntityId);
        f.AddCreditLine(WithBcra(f.AddEntity(), NoDebt, Now.AddHours(-1)).EntityId);
        await f.SaveAsync();

        var ars = Assert.Single((await PortfolioAsync(f)).BcraCurrent!);

        Assert.Equal(2, ars.StaleEntityCount);
        Assert.Equal(1, ars.NeverQueriedCount);
        Assert.Equal(day31, ars.OldestQueryAt);
    }

    [Fact]
    public async Task BcraCurrent_CountsAnEntityOncePerCurrency_AndItsStalenessInEach()
    {
        await using var f = DashboardFixture.Create(nameof(BcraCurrent_CountsAnEntityOncePerCurrency_AndItsStalenessInEach));
        var old = WithBcra(f.AddEntity(), Debt(2), Now.AddDays(-60));
        f.AddCreditLine(old.EntityId, "ARS", 100m);
        f.AddCreditLine(old.EntityId, "USD", 1m);
        await f.SaveAsync();

        var sections = (await PortfolioAsync(f)).BcraCurrent!;

        Assert.All(sections, c =>
        {
            Assert.Equal(((int?)2, 1), (Assert.Single(c.Buckets).Situacion, c.Buckets[0].EntityCount));
            Assert.Equal(1, c.StaleEntityCount);
        });
    }

    [Fact]
    public async Task BcraCurrent_ACupoWhoseEntityDoesNotResolve_KeepsItsMoney_UnderNull()
    {
        await using var f = DashboardFixture.Create(nameof(BcraCurrent_ACupoWhoseEntityDoesNotResolve_KeepsItsMoney_UnderNull));
        f.AddOperation(f.AddCreditLine(Guid.NewGuid(), amount: 300m), 120m);
        await f.SaveAsync();

        var ars = Assert.Single((await PortfolioAsync(f)).BcraCurrent!);

        var bucket = Assert.Single(ars.Buckets);
        Assert.Equal(((int?)null, 1, 300m, 120m), (bucket.Situacion, bucket.EntityCount, bucket.Otorgado, bucket.Utilizado));
        Assert.Equal(1, ars.NeverQueriedCount);
    }

    [Fact]
    public async Task BcraCurrent_IsTenantIsolated_OnCuposEntitiesAndOperations()
    {
        await using var f = DashboardFixture.Create(nameof(BcraCurrent_IsTenantIsolated_OnCuposEntitiesAndOperations));
        var mine = f.AddCreditLine(WithBcra(f.AddEntity(), NoDebt, Now).EntityId, amount: 1_000m);
        f.AddOperation(mine, 100m);
        f.AddOperation(mine, 9_000m, tenant: Other);                                   // tenant-2 row, tenant-1 cupo id
        f.AddCreditLine(WithBcra(f.AddEntity(tenant: Other), Debt(5), Now).EntityId, amount: 5m, tenant: Other);
        await f.SaveAsync();

        var bucket = Assert.Single(Assert.Single((await PortfolioAsync(f)).BcraCurrent!).Buckets);

        Assert.Equal(((int?)1, 1_000m, 100m), (bucket.Situacion, bucket.Otorgado, bucket.Utilizado));
    }

    [Fact]
    public async Task BcraCurrent_NoActiveCupo_IsAnEmptyList()
    {
        await using var f = DashboardFixture.Create(nameof(BcraCurrent_NoActiveCupo_IsAnEmptyList));
        await f.SaveAsync();

        var response = await PortfolioAsync(f);

        Assert.Empty(response.BcraCurrent!);
        Assert.Equal(0, response.AnnualReview!.OverdueCount);
        Assert.Equal(0m, response.Sgr!.SaldoGarantiasVigentesArs);
    }

    // ── Section isolation ─────────────────────────────────────────────────────

    public static TheoryData<string, string> NewSectionQueries => new()
    {
        { nameof(IDashboardRepository.GetBcraCurrentAsync),                             DashboardSections.BcraCurrent },
        { nameof(IDashboardRepository.GetLatestApprovedAnalysesOfActiveEntitiesAsync), DashboardSections.AnnualReview },
        { nameof(IDashboardRepository.GetOccupyingOperationsOfActiveLinesAsync),       DashboardSections.Sgr }
    };

    [Theory]
    [MemberData(nameof(NewSectionQueries))]
    public async Task AFailingQuery_DegradesOnlyItsSection(string method, string section)
    {
        await using var f = DashboardFixture.Create(nameof(AFailingQuery_DegradesOnlyItsSection) + method);
        var entity = WithBcra(f.AddEntity(), NoDebt, Now);
        f.AddOperation(f.AddCreditLine(entity.EntityId, amount: 100m), 95m);
        await f.SaveAsync();

        var response = await PortfolioAsync(f, repository: new FailingDashboardRepository(f.Repository(), method));

        Assert.Equal([section], response.DegradedSections);
        Assert.Equal(95m, Assert.Single(response.Totals!).Utilizado);
        Assert.NotNull(response.ByRisk);
        Assert.NotNull(response.TopExposures);
        Assert.NotNull(response.BySector);
        Assert.NotNull(response.Maturities);
        Assert.NotNull(response.BcraSignals);
        Assert.NotNull(response.HighUtilization);
        Assert.Equal(section == DashboardSections.BcraCurrent, response.BcraCurrent is null);
        Assert.Equal(section == DashboardSections.AnnualReview, response.AnnualReview is null);
        Assert.Equal(section == DashboardSections.Sgr, response.Sgr is null);
    }

    [Fact]
    public async Task ExistingSections_AreUnchanged_WhenANewSectionFails()
    {
        await using var f = DashboardFixture.Create(nameof(ExistingSections_AreUnchanged_WhenANewSectionFails));
        f.AddOperation(f.AddCreditLine(WithBcra(f.AddEntity(), Debt(2), Now).EntityId, amount: 100m), 95m);
        await f.SaveAsync();

        var healthy = await PortfolioAsync(f);
        var failing = await PortfolioAsync(f, repository: new FailingDashboardRepository(f.Repository(),
            nameof(IDashboardRepository.GetBcraCurrentAsync),
            nameof(IDashboardRepository.GetLatestApprovedAnalysesOfActiveEntitiesAsync),
            nameof(IDashboardRepository.GetOccupyingOperationsOfActiveLinesAsync)));

        Assert.Equal([DashboardSections.BcraCurrent, DashboardSections.AnnualReview, DashboardSections.Sgr], failing.DegradedSections);
        Assert.Equal(healthy.Totals, failing.Totals);
        Assert.Equal(healthy.HighUtilization!.Single().TotalCount, failing.HighUtilization!.Single().TotalCount);
    }
}
