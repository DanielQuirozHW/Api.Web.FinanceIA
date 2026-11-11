// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-31

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Covers <see cref="TenantBillingConfigService"/>: the in-code minimum plan when a tenant has no
/// active configuration, pass-through of a stored row's own limits, resilience when the read
/// itself fails, and per-request memoization (the Scoped instance queries the repository at most
/// once per tenant).
/// Follows the repo's no-mock-library convention: a hand-rolled private sealed stub.
///
/// <para>
/// The contract under test is that the method NEVER returns null and NEVER throws. Every failure
/// mode still yields <see cref="BillingPlans.Minimum"/> — but they are no longer
/// indistinguishable: the returned <see cref="BillingPlanResolution"/> says whether those numbers
/// are the tenant's real entitlement or a fallback, which is what lets enforcement fail open
/// instead of denying service to a paying customer over a blipped read.
/// </para>
///
/// <para>
/// The write half is tested for the OPPOSITE contract: <c>UpdateAsync</c> must let a failure
/// through. The two sit on one interface precisely so this asymmetry is visible.
/// </para>
/// </summary>
public sealed class TenantBillingConfigServiceTests
{
    private const string Tenant = "tenant-1";

    private sealed class StubTenantBillingConfigRepository : ITenantBillingConfigRepository
    {
        public TenantBillingConfig? ConfigToReturn { get; set; }
        public Exception? ThrowOnCall { get; set; }
        public int CallCount { get; private set; }

        public Task<TenantBillingConfig?> GetActiveByTenantAsync(string tenantId, CancellationToken ct)
        {
            CallCount++;
            if (ThrowOnCall is not null)
                return Task.FromException<TenantBillingConfig?>(ThrowOnCall);
            return Task.FromResult(ConfigToReturn);
        }

        public TenantBillingConfig? UpsertedConfig { get; private set; }
        public int UpsertCallCount { get; private set; }
        public Exception? ThrowOnUpsert { get; set; }

        public Task<TenantBillingConfig> UpsertAsync(TenantBillingConfig config, CancellationToken ct)
        {
            UpsertCallCount++;
            if (ThrowOnUpsert is not null)
                return Task.FromException<TenantBillingConfig>(ThrowOnUpsert);

            UpsertedConfig = config;

            // The real repository returns the persisted row, and a write makes the tenant's
            // configuration readable — so the stub also becomes the row a later read would find.
            ConfigToReturn = config;
            return Task.FromResult(config);
        }
    }

    private static TenantBillingConfigService BuildSut(StubTenantBillingConfigRepository repo)
        => new(repo, NullLogger<TenantBillingConfigService>.Instance);

    private static TenantBillingConfig Row(
        string planName = "200 análisis",
        int analyses = 200,
        long chatTokens = 8_000_000L,
        int contextBudget = 80_000,
        string? models = null) => new()
    {
        BillingConfigId         = Guid.NewGuid(),
        TenantId                = Tenant,
        PlanName                = planName,
        AnalysisMonthlyLimit    = analyses,
        ChatTokensMonthlyLimit  = chatTokens,
        ChatContextBudgetTokens = contextBudget,
        ChatEnabledModelsRaw    = models ?? ChatModelCatalog.DefaultCsv,
        IsActive                = true,
        UpdatedAt               = DateTime.UtcNow,
    };

    // ── Fallback to the in-code minimum ─────────────────────────────────────────

    [Fact]
    public async Task GetEffectiveAsync_WhenNoRow_ReturnsMinimumPlan()
    {
        var repo = new StubTenantBillingConfigRepository { ConfigToReturn = null };
        var sut  = BuildSut(repo);

        var resolution = await sut.GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.Equal(BillingPlans.Minimum, resolution.Plan);
        Assert.Equal(50, resolution.Plan.AnalysisMonthlyLimit);
        Assert.Equal(40_000, resolution.Plan.ChatContextBudgetTokens);
        Assert.Equal(BillingPlanSource.NoConfiguration, resolution.Source);
        Assert.False(resolution.IsDegraded);   // "no row" is a fact, not a failure
    }

    /// <summary>
    /// An inactive row is filtered out by the repository, so the service sees null and must resolve
    /// to the minimum — the same as a tenant that was never configured at all.
    /// </summary>
    [Fact]
    public async Task GetEffectiveAsync_WhenRowIsInactive_ReturnsMinimumPlan()
    {
        // The repository applies the IsActive predicate, so an inactive row reaches the service
        // as null. This test states the resulting contract at the service boundary.
        var repo = new StubTenantBillingConfigRepository { ConfigToReturn = null };
        var sut  = BuildSut(repo);

        var resolution = await sut.GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.Equal(BillingPlans.Minimum, resolution.Plan);
        Assert.Equal(BillingPlanSource.NoConfiguration, resolution.Source);
    }

    [Fact]
    public async Task GetEffectiveAsync_NeverReturnsNull()
    {
        var repo = new StubTenantBillingConfigRepository { ConfigToReturn = null };
        var sut  = BuildSut(repo);

        var resolution = await sut.GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.NotNull(resolution.Plan);
    }

    // ── Stored row wins ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetEffectiveAsync_WhenActiveRow_ReturnsTheRowsOwnLimits()
    {
        var repo = new StubTenantBillingConfigRepository { ConfigToReturn = Row() };
        var sut  = BuildSut(repo);

        var resolution = await sut.GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.Equal("200 análisis", resolution.Plan.PlanName);
        Assert.Equal(200, resolution.Plan.AnalysisMonthlyLimit);
        Assert.Equal(8_000_000L, resolution.Plan.ChatTokensMonthlyLimit);
        Assert.Equal(BillingPlanSource.Configured, resolution.Source);
        Assert.False(resolution.IsDegraded);
    }

    /// <summary>
    /// The row's columns are authoritative and PlanName is a LABEL, never a lookup key. A tenant
    /// priced off-preset — a name matching no preset, and numbers matching none either — keeps
    /// exactly what was stored. This is the test that would fail if someone later "helpfully"
    /// resolved the limits from <see cref="BillingPlans"/> via the name.
    /// </summary>
    [Fact]
    public async Task GetEffectiveAsync_WhenRowIsOffPreset_DoesNotSubstitutePresetValues()
    {
        var repo = new StubTenantBillingConfigRepository
        {
            ConfigToReturn = Row(planName: "Plan negociado a medida", analyses: 137, chatTokens: 12_345_678L)
        };
        var sut = BuildSut(repo);

        var resolution = await sut.GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.Equal("Plan negociado a medida", resolution.Plan.PlanName);
        Assert.Equal(137, resolution.Plan.AnalysisMonthlyLimit);
        Assert.Equal(12_345_678L, resolution.Plan.ChatTokensMonthlyLimit);
    }

    /// <summary>
    /// A stored row whose PlanName happens to match a preset must STILL be read from its own
    /// columns. Here the label says "50 análisis" but the row grants 999 — the row wins.
    /// </summary>
    [Fact]
    public async Task GetEffectiveAsync_WhenPlanNameMatchesAPreset_StillUsesTheRowsColumns()
    {
        var repo = new StubTenantBillingConfigRepository
        {
            ConfigToReturn = Row(planName: "50 análisis", analyses: 999, chatTokens: 1L, contextBudget: 99_000)
        };
        var sut = BuildSut(repo);

        var resolution = await sut.GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.Equal(999, resolution.Plan.AnalysisMonthlyLimit);
        Assert.Equal(1L, resolution.Plan.ChatTokensMonthlyLimit);

        // The newest column is read off the row for the same reason: "50 análisis" would have meant
        // 40.000 as a preset, and the negotiated 99.000 must not be overwritten by the label.
        Assert.Equal(99_000, resolution.Plan.ChatContextBudgetTokens);
    }

    // ── Chat model allow-list ───────────────────────────────────────────────────

    [Fact]
    public async Task GetEffectiveAsync_ReadsTheEnabledModelsOffTheRow()
    {
        var repo = new StubTenantBillingConfigRepository
        {
            ConfigToReturn = Row(models: "claude-sonnet-5,claude-haiku-4-5")
        };

        var resolution = await BuildSut(repo).GetEffectiveAsync(Tenant, CancellationToken.None);

        // Re-ordered to the catalogue on the way out: the FIRST id is the model a chat turn falls
        // back to, so it must not depend on the order the column happened to be written in.
        Assert.Equal(
            new[] { "claude-haiku-4-5", "claude-sonnet-5" },
            resolution.Plan.ChatEnabledModels);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gpt-4,bogus")]
    public async Task GetEffectiveAsync_ResolvesAnUnusableModelListToTheWholeCatalogue(string stored)
    {
        // Every one of these is reachable: a row written before script 052, a column a DBA blanked,
        // a list naming only models since retired. None may leave the tenant with no model — a chat
        // turn picks the FIRST enabled id, and an empty list would throw on that index.
        var repo = new StubTenantBillingConfigRepository { ConfigToReturn = Row(models: stored) };

        var resolution = await BuildSut(repo).GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.Equal(ChatModelCatalog.AllIds, resolution.Plan.ChatEnabledModels);
    }

    [Fact]
    public async Task GetEffectiveAsync_WhenNoRow_AllowsTheWholeCatalogueRatherThanTheLeast()
    {
        // The deliberate asymmetry with every other figure on the minimum plan. Those are allowances
        // and an unconfigured tenant gets the smallest; this is a capability the tenant already had
        // when the chat model was hardcoded, and the absence of a row must not take it away.
        var repo = new StubTenantBillingConfigRepository { ConfigToReturn = null };

        var resolution = await BuildSut(repo).GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.Equal(50, resolution.Plan.AnalysisMonthlyLimit);        // the least
        Assert.Equal(ChatModelCatalog.AllIds, resolution.Plan.ChatEnabledModels);  // the most
    }

    // ── Resilience ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The read still does not propagate — but it no longer passes itself off as a fact. This is
    /// the test phase 1 left behind for the enforcement PR to revisit: without the ReadFailed
    /// marker, a 200-plan tenant would be silently demoted to 50 by one failed SELECT and then
    /// refused its own analyses.
    /// </summary>
    [Fact]
    public async Task GetEffectiveAsync_WhenRepositoryThrows_ReturnsMinimumMarkedReadFailedAndDoesNotPropagate()
    {
        var repo = new StubTenantBillingConfigRepository
        {
            ThrowOnCall = new InvalidOperationException(
                "The connection does not support MultipleActiveResultSets.")
        };
        var sut = BuildSut(repo);

        var resolution = await sut.GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.Equal(BillingPlans.Minimum, resolution.Plan);
        Assert.Equal(40_000, resolution.Plan.ChatContextBudgetTokens);
        Assert.Equal(BillingPlanSource.ReadFailed, resolution.Source);
        Assert.True(resolution.IsDegraded);
    }

    /// <summary>
    /// A degraded resolution is memoized like any other, so a broken read is attempted once per
    /// request rather than once per quota gate.
    /// </summary>
    [Fact]
    public async Task GetEffectiveAsync_WhenRepositoryThrows_CalledTwice_QueriesRepositoryOnce()
    {
        var repo = new StubTenantBillingConfigRepository
        {
            ThrowOnCall = new InvalidOperationException("transient")
        };
        var sut = BuildSut(repo);

        var first  = await sut.GetEffectiveAsync(Tenant, CancellationToken.None);
        var second = await sut.GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.Equal(1, repo.CallCount);
        Assert.True(first.IsDegraded);
        Assert.True(second.IsDegraded);
    }

    /// <summary>
    /// The distinction that carries the whole fail-open policy: an unconfigured tenant and a
    /// failed read return the SAME limits, and must not be confused for one another.
    /// </summary>
    [Fact]
    public async Task GetEffectiveAsync_NoRowAndFailedRead_ShareLimitsButNotSource()
    {
        var absent = await BuildSut(new StubTenantBillingConfigRepository { ConfigToReturn = null })
            .GetEffectiveAsync(Tenant, CancellationToken.None);

        var failed = await BuildSut(new StubTenantBillingConfigRepository
            {
                ThrowOnCall = new InvalidOperationException("transient")
            })
            .GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.Equal(absent.Plan, failed.Plan);
        Assert.NotEqual(absent.Source, failed.Source);
        Assert.False(absent.IsDegraded);
        Assert.True(failed.IsDegraded);
    }

    // ── Memoization ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetEffectiveAsync_CalledTwice_QueriesRepositoryOnce()
    {
        var repo = new StubTenantBillingConfigRepository { ConfigToReturn = Row() };
        var sut  = BuildSut(repo);

        _ = await sut.GetEffectiveAsync(Tenant, CancellationToken.None);
        _ = await sut.GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.Equal(1, repo.CallCount);   // per-request memoization
    }

    /// <summary>The absent-configuration result is cached too, so an unconfigured tenant is not re-queried.</summary>
    [Fact]
    public async Task GetEffectiveAsync_WhenNoRow_CalledTwice_QueriesRepositoryOnce()
    {
        var repo = new StubTenantBillingConfigRepository { ConfigToReturn = null };
        var sut  = BuildSut(repo);

        _ = await sut.GetEffectiveAsync(Tenant, CancellationToken.None);
        _ = await sut.GetEffectiveAsync(Tenant, CancellationToken.None);

        Assert.Equal(1, repo.CallCount);
    }

    [Fact]
    public async Task GetEffectiveAsync_ForDifferentTenants_QueriesRepositoryPerTenant()
    {
        var repo = new StubTenantBillingConfigRepository { ConfigToReturn = null };
        var sut  = BuildSut(repo);

        _ = await sut.GetEffectiveAsync("tenant-a", CancellationToken.None);
        _ = await sut.GetEffectiveAsync("tenant-b", CancellationToken.None);

        Assert.Equal(2, repo.CallCount);
    }

    // ── Presets ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The minimum is the SMALLEST preset, not merely "a" preset: an unconfigured tenant must get
    /// the least, never the most.
    /// </summary>
    [Fact]
    public void BillingPlans_MinimumIsTheSmallestPreset()
    {
        Assert.Equal(BillingPlans.All.Min(p => p.AnalysisMonthlyLimit), BillingPlans.Minimum.AnalysisMonthlyLimit);
        Assert.Equal(BillingPlans.All.Min(p => p.ChatTokensMonthlyLimit), BillingPlans.Minimum.ChatTokensMonthlyLimit);
        Assert.Equal(BillingPlans.All.Min(p => p.ChatContextBudgetTokens), BillingPlans.Minimum.ChatContextBudgetTokens);
    }

    [Fact]
    public void BillingPlans_ExposesTheThreeCommercialTiers()
    {
        Assert.Equal([50, 100, 200], BillingPlans.All.Select(p => p.AnalysisMonthlyLimit));
    }

    /// <summary>
    /// The context budgets scale with the tier, like the token figures beside them: a larger plan
    /// buys a longer conversation. Placeholders until product settles them — this test states what
    /// they currently are, so changing one is a deliberate edit rather than a drift.
    /// </summary>
    [Fact]
    public void BillingPlans_ContextBudgetsScaleWithTheTier()
    {
        Assert.Equal([40_000, 60_000, 80_000], BillingPlans.All.Select(p => p.ChatContextBudgetTokens));
    }

    [Fact]
    public void BillingPlans_Find_MatchesByNameCaseInsensitivelyAndReturnsNullOtherwise()
    {
        Assert.Equal(BillingPlans.Plan100, BillingPlans.Find("  100 Análisis  "));
        Assert.Null(BillingPlans.Find("Plan negociado a medida"));
        Assert.Null(BillingPlans.Find(null));
        Assert.Null(BillingPlans.Find("   "));
    }

    // ── Write path (UpdateAsync) ────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_StampsUpdatedByAndUtcUpdatedAt()
    {
        var repo   = new StubTenantBillingConfigRepository();
        var before = DateTime.UtcNow;

        var saved = await BuildSut(repo).UpdateAsync(
            Tenant, BillingPlans.Plan200, isActive: true, updatedBy: "the-superadmin", CancellationToken.None);

        Assert.Equal("the-superadmin", saved.UpdatedBy);
        Assert.InRange(saved.UpdatedAt, before, DateTime.UtcNow);
        Assert.Equal(DateTimeKind.Utc, saved.UpdatedAt.Kind);
    }

    [Fact]
    public async Task UpdateAsync_PassesThePlanAndTenantThroughToTheRepository()
    {
        var repo = new StubTenantBillingConfigRepository();

        await BuildSut(repo).UpdateAsync(
            Tenant,
            new BillingPlan("Plan negociado", 137, 5_500_000L, 55_000, ChatModelCatalog.AllIds),
            isActive: false,
            updatedBy: "the-superadmin",
            CancellationToken.None);

        var written = Assert.IsType<TenantBillingConfig>(repo.UpsertedConfig);
        Assert.Equal(Tenant,           written.TenantId);
        Assert.Equal("Plan negociado", written.PlanName);
        Assert.Equal(137,              written.AnalysisMonthlyLimit);
        Assert.Equal(5_500_000L,       written.ChatTokensMonthlyLimit);
        Assert.Equal(55_000,           written.ChatContextBudgetTokens);
        Assert.False(written.IsActive);

        // The list reaches the entity as the CSV the column stores, not as a list the entity would
        // have no way to persist.
        Assert.Equal(ChatModelCatalog.DefaultCsv, written.ChatEnabledModelsRaw);
    }

    [Fact]
    public async Task UpdateAsync_WritesARestrictedModelListAsCsv()
    {
        var repo = new StubTenantBillingConfigRepository();

        await BuildSut(repo).UpdateAsync(
            Tenant,
            new BillingPlan("Plan negociado", 137, 5_500_000L, 55_000,
                            ["claude-haiku-4-5", "claude-sonnet-4-6"]),
            isActive: true,
            updatedBy: "the-superadmin",
            CancellationToken.None);

        var written = Assert.IsType<TenantBillingConfig>(repo.UpsertedConfig);

        Assert.Equal("claude-haiku-4-5,claude-sonnet-4-6", written.ChatEnabledModelsRaw);
        Assert.Equal(new[] { "claude-haiku-4-5", "claude-sonnet-4-6" }, written.ChatEnabledModels);
    }

    /// <summary>
    /// The mirror image of the read path's fail-open. A swallowed write would report a plan change
    /// that never reached the database.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_WhenTheRepositoryFails_PropagatesInsteadOfFallingBackToMinimum()
    {
        var repo = new StubTenantBillingConfigRepository
        {
            ThrowOnUpsert = new InvalidOperationException("db down")
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => BuildSut(repo).UpdateAsync(
            Tenant, BillingPlans.Plan200, isActive: true, updatedBy: "the-superadmin", CancellationToken.None));
    }

    /// <summary>
    /// The instance is Scoped, so it outlives the write inside one request. Without invalidation
    /// the memoized resolution from a read before the write would keep serving the old plan.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_InvalidatesTheMemoizedResolutionForThatTenant()
    {
        var repo = new StubTenantBillingConfigRepository
        {
            ConfigToReturn = Row(planName: "50 análisis", analyses: 50, chatTokens: 2_000_000L)
        };
        var sut = BuildSut(repo);

        var before = await sut.GetEffectiveAsync(Tenant, CancellationToken.None);
        Assert.Equal(50, before.Plan.AnalysisMonthlyLimit);

        await sut.UpdateAsync(Tenant, BillingPlans.Plan200, isActive: true, updatedBy: "the-superadmin", CancellationToken.None);

        var after = await sut.GetEffectiveAsync(Tenant, CancellationToken.None);
        Assert.Equal(200, after.Plan.AnalysisMonthlyLimit);
        Assert.Equal("200 análisis", after.Plan.PlanName);
        Assert.Equal(BillingPlanSource.Configured, after.Source);
    }

    [Fact]
    public async Task UpdateAsync_DoesNotInvalidateAnotherTenantsCachedResolution()
    {
        var repo = new StubTenantBillingConfigRepository
        {
            ConfigToReturn = Row(planName: "50 análisis", analyses: 50, chatTokens: 2_000_000L)
        };
        var sut = BuildSut(repo);

        await sut.GetEffectiveAsync("tenant-2", CancellationToken.None);
        var readsAfterFirst = repo.CallCount;

        await sut.UpdateAsync(Tenant, BillingPlans.Plan200, isActive: true, updatedBy: "the-superadmin", CancellationToken.None);
        await sut.GetEffectiveAsync("tenant-2", CancellationToken.None);

        Assert.Equal(readsAfterFirst, repo.CallCount);
    }
}
