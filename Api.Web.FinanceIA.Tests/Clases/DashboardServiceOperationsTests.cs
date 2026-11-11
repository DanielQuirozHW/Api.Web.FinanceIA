// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-23

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// <see cref="DashboardService.GetOperationsAsync"/>: what ApiCallLogs can say per service — and, for the
/// vendors that write no row, that it cannot — plus snapshot freshness.
/// </summary>
public sealed class DashboardServiceOperationsTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTime Now = DashboardFixture.Now;
    private const string Tenant = DashboardFixture.Tenant;

    private static Task<OperationsResponse> OperationsAsync(
        DashboardFixture f, IDashboardRepository? repository = null, IIntegrationAccessService? access = null)
        => f.Service(repository, access).GetOperationsAsync(Tenant, Now, Ct);

    private static IEnumerable<string> ServiceNames(OperationsResponse r) => r.Integrations!.Services.Select(s => s.Service);
    private static IEnumerable<string> SourceNames(OperationsResponse r)  => r.Freshness!.Sources.Select(s => s.Source);

    private static Dictionary<string, string?> ConfiguredWithout(string key)
    {
        var settings = DashboardFixture.FullyConfigured();
        settings.Remove(key);
        return settings;
    }

    [Fact]
    public async Task Integrations_ListEveryWriterEvenIdle_ThenBcraAndCasfogAsUntracked_ThenAnyOtherValue()
    {
        await using var f = DashboardFixture.Create(nameof(Integrations_ListEveryWriterEvenIdle_ThenBcraAndCasfogAsUntracked_ThenAnyOtherValue));
        f.AddApiCall("BYMA", true, Now.AddHours(-1));
        await f.SaveAsync();

        var services = (await OperationsAsync(f)).Integrations!.Services;

        Assert.Equal(["ARCA", "LUFE", "NOSIS", "Claude", "ClaudeChat", "BCRA", "CASFOG", "BYMA"], services.Select(s => s.Service));
        Assert.All(services.Take(5), s => Assert.Equal((true, 0, 0, (int?)null), (s.Tracked, s.Calls!.Value, s.Failures!.Value, s.P95DurationMs)));
        foreach (var untracked in services.Where(s => s.Service is "BCRA" or "CASFOG"))
        {
            Assert.False(untracked.Tracked);
            Assert.Null(untracked.Calls);
            Assert.Null(untracked.Failures);
            Assert.Null(untracked.LastSuccessAt);
            Assert.Null(untracked.P95DurationMs);
        }
        Assert.True(services[^1].Tracked);
        Assert.Equal(1, services[^1].Calls);
    }

    [Fact]
    public async Task Integrations_CountTheLast24Hours_WithLastSuccessLastFailureAndNearestRankP95()
    {
        await using var f = DashboardFixture.Create(nameof(Integrations_CountTheLast24Hours_WithLastSuccessLastFailureAndNearestRankP95));
        for (var i = 1; i <= 20; i++)
            f.AddApiCall("NOSIS", true, Now.AddMinutes(-i), durationMs: i * 10);
        f.AddApiCall("NOSIS", false, Now.AddHours(-2), durationMs: null);
        f.AddApiCall("NOSIS", false, Now.AddHours(-25));
        f.AddApiCall("NOSIS", true, Now, durationMs: 99_999, tenant: DashboardFixture.Other);
        await f.SaveAsync();

        var integrations = (await OperationsAsync(f)).Integrations!;
        var nosis = integrations.Services.Single(s => s.Service == "NOSIS");

        Assert.Equal((Now.AddHours(-24), Now), (integrations.WindowStartUtc, integrations.WindowEndUtc));
        Assert.Equal((21, 1), (nosis.Calls, nosis.Failures));
        Assert.Equal((Now.AddMinutes(-1), Now.AddHours(-2)), (nosis.LastSuccessAt, nosis.LastFailureAt));
        Assert.Equal(190, nosis.P95DurationMs);   // ceil(0.95 * 20) = 19th of 10..200
    }

    // Production, FinanciaMovilSGR: one LUFE entidades/{cuit} → 404 "No legajo" painted the panel "Con fallas".
    [Fact]
    public async Task Lufe_ANoLegajo4xx_IsAnAnsweredCall_NotAFailure_AndMovesLastSuccess()
    {
        await using var f = DashboardFixture.Create(nameof(Lufe_ANoLegajo4xx_IsAnAnsweredCall_NotAFailure_AndMovesLastSuccess));
        f.AddApiCall("LUFE", true,  Now.AddHours(-5), responseStatus: 200);
        f.AddApiCall("LUFE", false, Now.AddHours(-3), responseStatus: 500, errorMessage: "Lookup failed (HTTP 500)");
        f.AddApiCall("LUFE", false, Now.AddHours(-1), responseStatus: 404, errorMessage: "No legajo (HTTP 404)");
        await f.SaveAsync();

        var lufe = (await OperationsAsync(f)).Integrations!.Services.Single(s => s.Service == "LUFE");

        Assert.Equal((3, 1, 1), (lufe.Calls!.Value, lufe.Failures!.Value, lufe.NoData!.Value));
        Assert.Equal((Now.AddHours(-1), Now.AddHours(-3)), (lufe.LastSuccessAt, lufe.LastFailureAt));
    }

    [Fact]
    public async Task Lufe_OnlyNoLegajoAnswers_HasNoFailureAndNoLastFailure()
    {
        await using var f = DashboardFixture.Create(nameof(Lufe_OnlyNoLegajoAnswers_HasNoFailureAndNoLastFailure));
        f.AddApiCall("LUFE", false, Now.AddHours(-2), responseStatus: 404, errorMessage: "No legajo (HTTP 404)");
        f.AddApiCall("LUFE", false, Now.AddHours(-2), responseStatus: 404, errorMessage: "No data (HTTP 404)");
        await f.SaveAsync();

        var lufe = (await OperationsAsync(f)).Integrations!.Services.Single(s => s.Service == "LUFE");

        Assert.Equal((2, 0, 2), (lufe.Calls!.Value, lufe.Failures!.Value, lufe.NoData!.Value));
        Assert.Equal(Now.AddHours(-2), lufe.LastSuccessAt);
        Assert.Null(lufe.LastFailureAt);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(200)]   // a malformed 200 body ("Parse error") is a failure, not an empty answer
    [InlineData(null)]  // no response at all: network error or timeout
    public async Task Lufe_ARealFailure_KeepsCountingAsAFailure(int? status)
    {
        await using var f = DashboardFixture.Create($"{nameof(Lufe_ARealFailure_KeepsCountingAsAFailure)}-{status}");
        f.AddApiCall("LUFE", true,  Now.AddHours(-2), responseStatus: 200);
        f.AddApiCall("LUFE", false, Now.AddHours(-1), responseStatus: status);
        await f.SaveAsync();

        var lufe = (await OperationsAsync(f)).Integrations!.Services.Single(s => s.Service == "LUFE");

        Assert.Equal((2, 1, 0), (lufe.Calls!.Value, lufe.Failures!.Value, lufe.NoData!.Value));
        Assert.Equal((Now.AddHours(-2), Now.AddHours(-1)), (lufe.LastSuccessAt, lufe.LastFailureAt));
    }

    // Production, SgrOneClickSandboxFB2: 52 sub-section rows "Empty (HTTP 200)" counted as failures. Lufe
    // answered 200 with nothing usable — no data; a malformed 200 ("Parse error") is still a failure.
    [Fact]
    public async Task Lufe_A200WithNothingUsable_IsNoData_AParseError_IsAFailure()
    {
        await using var f = DashboardFixture.Create(nameof(Lufe_A200WithNothingUsable_IsNoData_AParseError_IsAFailure));
        f.AddApiCall("LUFE", false, Now.AddHours(-1), responseStatus: 200, errorMessage: LufeCallOutcome.DescribeEmptyAnswer(200));
        f.AddApiCall("LUFE", false, Now.AddHours(-2), responseStatus: 200, errorMessage: LufeCallOutcome.DescribeNoLegajo(200));
        f.AddApiCall("LUFE", false, Now.AddHours(-3), responseStatus: 200, errorMessage: "Parse error");
        await f.SaveAsync();

        var lufe = (await OperationsAsync(f)).Integrations!.Services.Single(s => s.Service == "LUFE");

        Assert.Equal((3, 1, 2), (lufe.Calls!.Value, lufe.Failures!.Value, lufe.NoData!.Value));
        Assert.Equal((Now.AddHours(-1), Now.AddHours(-3)), (lufe.LastSuccessAt, lufe.LastFailureAt));
    }

    [Fact]
    public async Task Arca_ANotFound_IsNoData_AFailedCall_IsAFailure()
    {
        await using var f = DashboardFixture.Create(nameof(Arca_ANotFound_IsNoData_AFailedCall_IsAFailure));
        f.AddApiCall("ARCA", false, Now.AddHours(-1), responseStatus: 502, errorMessage: null);
        f.AddApiCall("ARCA", false, Now.AddHours(-4), responseStatus: 502, errorMessage: "HttpRequestException");
        await f.SaveAsync();

        var arca = (await OperationsAsync(f)).Integrations!.Services.Single(s => s.Service == "ARCA");

        Assert.Equal((2, 1, 1), (arca.Calls!.Value, arca.Failures!.Value, arca.NoData!.Value));
        Assert.Equal((Now.AddHours(-1), Now.AddHours(-4)), (arca.LastSuccessAt, arca.LastFailureAt));
    }

    [Theory]
    [InlineData("NOSIS")]
    [InlineData("Claude")]
    [InlineData("ClaudeChat")]
    public async Task OtherServices_A4xxWithoutErrorText_IsStillAFailure(string service)
    {
        await using var f = DashboardFixture.Create($"{nameof(OtherServices_A4xxWithoutErrorText_IsStillAFailure)}-{service}");
        f.AddApiCall(service, false, Now.AddHours(-1), responseStatus: 404, errorMessage: null);
        await f.SaveAsync();

        var item = (await OperationsAsync(f)).Integrations!.Services.Single(s => s.Service == service);

        Assert.Equal((1, 1, 0), (item.Calls!.Value, item.Failures!.Value, item.NoData!.Value));
        Assert.Null(item.LastSuccessAt);
    }

    [Fact]
    public async Task Untracked_Vendors_HaveNoNoDataFigure()
    {
        await using var f = DashboardFixture.Create(nameof(Untracked_Vendors_HaveNoNoDataFigure));
        await f.SaveAsync();

        var services = (await OperationsAsync(f)).Integrations!.Services;

        Assert.All(services.Where(s => !s.Tracked), s => Assert.Null(s.NoData));
        Assert.All(services.Where(s => s.Tracked), s => Assert.Equal(0, s.NoData));
    }

    [Theory]
    [InlineData(new int[0], null)]
    [InlineData(new[] { 7 }, 7)]
    [InlineData(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, 10)]
    public void NearestRank_IsAnObservedValue(int[] sorted, int? expected)
    {
        Assert.Equal(expected, DashboardService.NearestRank(sorted, 0.95));
    }

    [Fact]
    public async Task Freshness_PassesThePopulationAndTheBands()
    {
        await using var f = DashboardFixture.Create(nameof(Freshness_PassesThePopulationAndTheBands));
        var fresh = f.AddEntity();
        fresh.LufeDataUpdatedAt = Now.AddDays(-3);
        f.AddCreditLine(fresh.EntityId);
        var stale = f.AddEntity();
        stale.LufeDataUpdatedAt = Now.AddDays(-120);
        f.AddCreditLine(stale.EntityId);
        await f.SaveAsync();

        var freshness = (await OperationsAsync(f)).Freshness!;

        Assert.Equal(2, freshness.EntityCount);
        var lufe = freshness.Sources.Single(s => s.Source == "LUFE");
        Assert.Equal((1, 0, 1, 0), (lufe.Under30Days, lufe.Days30To90, lufe.Over90Days, lufe.Never));
        Assert.Equal(["ARCA", "LUFE", "BCRA", "CASFOG", "NOSIS"], freshness.Sources.Select(s => s.Source));
    }

    // ── Only the integrations the tenant can use ─────────────────────────────────

    [Theory]
    [InlineData("Nosis:Enabled")]
    [InlineData("Nosis:ApiKey")]
    [InlineData("Nosis:VR")]
    public async Task AnSgrTenant_WithNosisNotConfigured_ListsNoNosis_EvenWithOldNosisRowsInTheWindow(string missingKey)
    {
        await using var f = DashboardFixture.Create(nameof(AnSgrTenant_WithNosisNotConfigured_ListsNoNosis_EvenWithOldNosisRowsInTheWindow) + missingKey);
        f.AddApiCall("NOSIS", false, Now.AddHours(-1));
        await f.SaveAsync();

        var response = await OperationsAsync(f, access: DashboardFixture.IntegrationAccess(settings: ConfiguredWithout(missingKey)));

        Assert.Equal(["ARCA", "LUFE", "Claude", "ClaudeChat", "BCRA", "CASFOG"], ServiceNames(response));
        Assert.Equal(["ARCA", "LUFE", "BCRA", "CASFOG"], SourceNames(response));
    }

    [Fact]
    public async Task ANonSgrTenant_ListsNoCasfogAndNoNosis_OnAFullyConfiguredInstallation()
    {
        await using var f = DashboardFixture.Create(nameof(ANonSgrTenant_ListsNoCasfogAndNoNosis_OnAFullyConfiguredInstallation));
        await f.SaveAsync();

        var nonSgr   = new TenantProfileResolution(TenantProfilePresets.NonSgr, TenantProfileSource.Configured);
        var response = await OperationsAsync(f, access: DashboardFixture.IntegrationAccess(nonSgr));

        Assert.Equal(["ARCA", "LUFE", "Claude", "ClaudeChat", "BCRA"], ServiceNames(response));
        Assert.Equal(["ARCA", "LUFE", "BCRA"], SourceNames(response));
    }

    [Fact]
    public async Task ADegradedProfile_FailsOpen_AndListsEverything()
    {
        await using var f = DashboardFixture.Create(nameof(ADegradedProfile_FailsOpen_AndListsEverything));
        await f.SaveAsync();

        // A NON-SGR profile marked degraded: were its flags trusted, CASFOG and NOSIS would vanish.
        var degraded = new TenantProfileResolution(TenantProfilePresets.NonSgr, TenantProfileSource.ReadFailed);
        var response = await OperationsAsync(f, access: DashboardFixture.IntegrationAccess(degraded));

        Assert.Empty(response.DegradedSections);
        Assert.Equal(["ARCA", "LUFE", "NOSIS", "Claude", "ClaudeChat", "BCRA", "CASFOG"], ServiceNames(response));
        Assert.Equal(["ARCA", "LUFE", "BCRA", "CASFOG", "NOSIS"], SourceNames(response));
    }

    [Fact]
    public async Task AProfileReadThatThrows_FailsOpen_AndListsEverything()
    {
        await using var f = DashboardFixture.Create(nameof(AProfileReadThatThrows_FailsOpen_AndListsEverything));
        await f.SaveAsync();

        var response = await OperationsAsync(f,
            access: DashboardFixture.IntegrationAccess(throwOnRead: new InvalidOperationException("boom")));

        Assert.Empty(response.DegradedSections);
        Assert.Equal(["ARCA", "LUFE", "NOSIS", "Claude", "ClaudeChat", "BCRA", "CASFOG"], ServiceNames(response));
        Assert.Equal(["ARCA", "LUFE", "BCRA", "CASFOG", "NOSIS"], SourceNames(response));
    }

    [Fact]
    public async Task Lufe_WithoutATenantKey_IsNotListed()
    {
        await using var f = DashboardFixture.Create(nameof(Lufe_WithoutATenantKey_IsNotListed));
        await f.SaveAsync();

        var response = await OperationsAsync(f,
            access: DashboardFixture.IntegrationAccess(settings: ConfiguredWithout($"Tenants:{Tenant}:LufeApiKey")));

        Assert.Equal(["ARCA", "NOSIS", "Claude", "ClaudeChat", "BCRA", "CASFOG"], ServiceNames(response));
        Assert.Equal(["ARCA", "BCRA", "CASFOG", "NOSIS"], SourceNames(response));
    }

    [Fact]
    public async Task Casfog_WithoutCredentials_IsNotListed()
    {
        await using var f = DashboardFixture.Create(nameof(Casfog_WithoutCredentials_IsNotListed));
        await f.SaveAsync();

        var response = await OperationsAsync(f,
            access: DashboardFixture.IntegrationAccess(settings: ConfiguredWithout("Casfog:Password")));

        Assert.DoesNotContain("CASFOG", ServiceNames(response));
        Assert.DoesNotContain("CASFOG", SourceNames(response));
    }

    [Fact]
    public async Task ACancelledProfileRead_IsNotADegradedProfile_AndPropagates()
    {
        await using var f = DashboardFixture.Create(nameof(ACancelledProfileRead_IsNotADegradedProfile_AndPropagates));
        await f.SaveAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => OperationsAsync(f,
            access: DashboardFixture.IntegrationAccess(throwOnRead: new OperationCanceledException())));
    }

    [Fact]
    public async Task AFailingSection_IsNullAndListed_TheRestStillReturns()
    {
        await using var f = DashboardFixture.Create(nameof(AFailingSection_IsNullAndListed_TheRestStillReturns));
        await f.SaveAsync();

        var failing  = new FailingDashboardRepository(f.Repository(), nameof(IDashboardRepository.GetApiCallDurationsAsync));
        var response = await OperationsAsync(f, failing);

        Assert.Equal([DashboardSections.Integrations], response.DegradedSections);
        Assert.Null(response.Integrations);
        Assert.NotNull(response.Freshness);
    }

    [Fact]
    public async Task Cancellation_IsNotASectionFailure()
    {
        await using var f = DashboardFixture.Create(nameof(Cancellation_IsNotASectionFailure));
        await f.SaveAsync();

        var cancelling = new FailingDashboardRepository(f.Repository(), nameof(IDashboardRepository.GetFreshnessAsync))
        {
            Throw = () => new OperationCanceledException()
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => OperationsAsync(f, cancelling));
    }
}
