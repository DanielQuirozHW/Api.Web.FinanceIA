// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-31

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Query-level tests for <see cref="TenantBillingConfigRepository"/>, run against the EF Core
/// in-memory provider (a database provider, not a mocking library) so the actual WHERE clause is
/// exercised — behaviour a hand-rolled stub cannot verify, because a stub returns whatever it is
/// handed.
///
/// <para>
/// TENANT ISOLATION IS PROVEN HERE, not in the service tests, and for exactly that reason: the
/// service tests stub this repository away, so they can show the service passes a tenant id but
/// never that the query honours it. This matters more than usual for this type — the project has
/// NO EF global query filter, so the <c>TenantId</c> predicate in the repository is the only
/// thing standing between one client's commercial terms and another's.
/// </para>
///
/// <para>
/// The other behaviour locked down: an <c>IsActive = false</c> row is invisible, exactly like an
/// absent one. Both mean "no configuration", so both let the caller fall through to the in-code
/// minimum plan; a switched-off row must never resolve as if it were in force.
/// </para>
///
/// <para>
/// The upsert tests turn that same fact into its opposite obligation. An inactive row is invisible
/// to the READ but is still the tenant's one row under UQ_TenantBillingConfig_Tenant, so the WRITE
/// has to see it and update it. A write built on the active-only read would insert a second row
/// and hit the constraint — the one bug this file exists to prevent.
/// </para>
/// </summary>
public sealed class TenantBillingConfigRepositoryTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";

    // ── Helpers ─────────────────────────────────────────────────────────────────
    private static FinanciaDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"billing-{Guid.NewGuid()}")
            .Options;
        return new FinanciaDbContext(options);
    }

    private static TenantBillingConfig Config(
        string tenantId = Tenant,
        string planName = "100 análisis",
        int analyses = 100,
        long chatTokens = 4_000_000L,
        int contextBudget = 60_000,
        string? models = null,
        bool isActive = true) => new()
    {
        BillingConfigId         = Guid.NewGuid(),
        TenantId                = tenantId,
        PlanName                = planName,
        AnalysisMonthlyLimit    = analyses,
        ChatTokensMonthlyLimit  = chatTokens,
        ChatContextBudgetTokens = contextBudget,
        ChatEnabledModelsRaw    = models ?? ChatModelCatalog.DefaultCsv,
        IsActive                = isActive,
        UpdatedAt               = DateTime.UtcNow,
        UpdatedBy               = "dba",
    };

    private static async Task SeedAsync(FinanciaDbContext ctx, params TenantBillingConfig[] configs)
    {
        ctx.TenantBillingConfigs.AddRange(configs);
        ctx.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await ctx.SaveChangesAsync();
    }

    private static TenantBillingConfigRepository Repo(FinanciaDbContext ctx)
        => new(ctx, NullLogger<TenantBillingConfigRepository>.Instance);

    // ── GetActiveByTenantAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task GetActiveByTenant_ReturnsTheActiveRow()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Config());

        var result = await Repo(ctx).GetActiveByTenantAsync(Tenant, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("100 análisis", result.PlanName);
        Assert.Equal(100, result.AnalysisMonthlyLimit);
        Assert.Equal(4_000_000L, result.ChatTokensMonthlyLimit);
    }

    [Fact]
    public async Task GetActiveByTenant_WhenNoRow_ReturnsNull()
    {
        using var ctx = NewContext();

        var result = await Repo(ctx).GetActiveByTenantAsync(Tenant, CancellationToken.None);

        Assert.Null(result);
    }

    /// <summary>A switched-off configuration is not in force — it reads as no configuration at all.</summary>
    [Fact]
    public async Task GetActiveByTenant_ExcludesInactiveRow()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Config(isActive: false));

        var result = await Repo(ctx).GetActiveByTenantAsync(Tenant, CancellationToken.None);

        Assert.Null(result);
    }

    /// <summary>
    /// THE tenant test. Another client's billing terms are not merely filtered from a response —
    /// they never leave the database. With no global query filter in this project, this assertion
    /// is the whole guarantee.
    /// </summary>
    [Fact]
    public async Task GetActiveByTenant_IsTenantIsolated()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Config(tenantId: Other, planName: "200 análisis", analyses: 200));

        var result = await Repo(ctx).GetActiveByTenantAsync(Tenant, CancellationToken.None);

        Assert.Null(result);
    }

    /// <summary>
    /// With rows for several tenants present, the read returns the caller's own — not merely
    /// "something". Guards against a predicate that filters on IsActive but forgets TenantId.
    /// </summary>
    [Fact]
    public async Task GetActiveByTenant_WithSeveralTenants_ReturnsTheCallersOwn()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Config(tenantId: Other,  planName: "200 análisis", analyses: 200),
            Config(tenantId: Tenant, planName: "50 análisis",  analyses: 50));

        var result = await Repo(ctx).GetActiveByTenantAsync(Tenant, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(Tenant, result.TenantId);
        Assert.Equal(50, result.AnalysisMonthlyLimit);
    }

    /// <summary>
    /// ChatTokensMonthlyLimit is BIGINT precisely so a monthly pool can exceed int.MaxValue.
    /// A value above that ceiling must survive the round trip intact.
    /// </summary>
    [Fact]
    public async Task GetActiveByTenant_PreservesChatTokenLimitAboveIntMaxValue()
    {
        const long BeyondInt = 5_000_000_000L;   // > int.MaxValue (2_147_483_647)

        using var ctx = NewContext();
        await SeedAsync(ctx, Config(chatTokens: BeyondInt));

        var result = await Repo(ctx).GetActiveByTenantAsync(Tenant, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(BeyondInt, result.ChatTokensMonthlyLimit);
    }

    /// <summary>
    /// The context budget is a stored column like any other, not derived from PlanName: a row whose
    /// label says "50 análisis" but whose budget was negotiated at 99.000 must read back as 99.000.
    /// </summary>
    [Fact]
    public async Task GetActiveByTenant_ReturnsTheRowsOwnContextBudget()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Config(planName: "50 análisis", analyses: 50, contextBudget: 99_000));

        var result = await Repo(ctx).GetActiveByTenantAsync(Tenant, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(99_000, result.ChatContextBudgetTokens);
    }

    // ── Upsert ──────────────────────────────────────────────────────────────────

    private static TenantBillingConfig Incoming(
        string tenantId = Tenant,
        string planName = "200 análisis",
        int analyses = 200,
        long chatTokens = 8_000_000L,
        int contextBudget = 80_000,
        string? models = null,
        bool isActive = true,
        string? updatedBy = "superadmin-1") => new()
    {
        // BillingConfigId deliberately left unset: the store assigns it via NEWSEQUENTIALID(),
        // which is exactly how the service builds the entity.
        TenantId                = tenantId,
        PlanName                = planName,
        AnalysisMonthlyLimit    = analyses,
        ChatTokensMonthlyLimit  = chatTokens,
        ChatContextBudgetTokens = contextBudget,
        ChatEnabledModelsRaw    = models ?? ChatModelCatalog.DefaultCsv,
        IsActive                = isActive,
        UpdatedAt               = DateTime.UtcNow,
        UpdatedBy               = updatedBy
    };

    [Fact]
    public async Task Upsert_WhenNoRowExists_InsertsOneAndAssignsTheKey()
    {
        using var ctx = NewContext();

        var saved = await Repo(ctx).UpsertAsync(Incoming(), CancellationToken.None);

        Assert.NotEqual(Guid.Empty, saved.BillingConfigId);
        Assert.Equal("200 análisis", saved.PlanName);

        var rows = await ctx.TenantBillingConfigs.AsNoTracking().ToListAsync();
        Assert.Single(rows);
        Assert.Equal(200,         rows[0].AnalysisMonthlyLimit);
        Assert.Equal(8_000_000L,  rows[0].ChatTokensMonthlyLimit);
        Assert.Equal(80_000,      rows[0].ChatContextBudgetTokens);
        Assert.Equal("superadmin-1", rows[0].UpdatedBy);
        Assert.True(rows[0].IsActive);
    }

    [Fact]
    public async Task Upsert_WhenAnActiveRowExists_UpdatesItInPlace()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Config(planName: "100 análisis", analyses: 100, chatTokens: 4_000_000L));

        await Repo(ctx).UpsertAsync(Incoming(), CancellationToken.None);

        var rows = await ctx.TenantBillingConfigs.AsNoTracking().Where(x => x.TenantId == Tenant).ToListAsync();
        Assert.Single(rows);
        Assert.Equal("200 análisis", rows[0].PlanName);
        Assert.Equal(200,            rows[0].AnalysisMonthlyLimit);
        Assert.Equal(8_000_000L,     rows[0].ChatTokensMonthlyLimit);
        Assert.Equal(80_000,         rows[0].ChatContextBudgetTokens);
        Assert.Equal("superadmin-1", rows[0].UpdatedBy);
    }

    /// <summary>
    /// The update branch copies every column by hand, so a column added to the entity and forgotten
    /// there is not a compile error — it is an UPDATE that silently keeps the old value. This states
    /// that contract for the newest column (script 051): a seeded row on the 100 tier moved to the
    /// 200 tier must come back with the 200 tier's context budget, not the one it started with.
    /// </summary>
    [Fact]
    public async Task Upsert_WhenUpdatingAnExistingRow_OverwritesTheContextBudget()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Config(planName: "100 análisis", analyses: 100, contextBudget: 60_000));

        await Repo(ctx).UpsertAsync(Incoming(contextBudget: 80_000), CancellationToken.None);

        var rows = await ctx.TenantBillingConfigs.AsNoTracking().Where(x => x.TenantId == Tenant).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(80_000, rows[0].ChatContextBudgetTokens);
    }

    /// <summary>
    /// The same contract for the newest column (script 052), and the one where a forgotten line in
    /// the hand-copied update block would be hardest to notice: a SuperAdmin who restricts a tenant
    /// to two models would be told the change saved, the endpoint would echo the restricted list back
    /// from the entity it built, and the row would still permit all three. Nothing would error, and
    /// the analyst would keep seeing the model the SuperAdmin thought they had removed.
    /// </summary>
    [Fact]
    public async Task Upsert_WhenUpdatingAnExistingRow_OverwritesTheEnabledModels()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Config(models: ChatModelCatalog.DefaultCsv));

        await Repo(ctx).UpsertAsync(
            Incoming(models: "claude-haiku-4-5,claude-sonnet-4-6"), CancellationToken.None);

        var rows = await ctx.TenantBillingConfigs.AsNoTracking().Where(x => x.TenantId == Tenant).ToListAsync();
        Assert.Single(rows);
        Assert.Equal("claude-haiku-4-5,claude-sonnet-4-6", rows[0].ChatEnabledModelsRaw);
        Assert.Equal(new[] { "claude-haiku-4-5", "claude-sonnet-4-6" }, rows[0].ChatEnabledModels);
    }

    /// <summary>
    /// The read side of the same column: what comes back out of the store is the CSV, and the parsed
    /// view has to survive a round trip through the database rather than only working on an entity
    /// that never left memory.
    /// </summary>
    [Fact]
    public async Task GetActiveByTenant_ParsesTheStoredModelListBackToIds()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Config(models: "claude-sonnet-5,claude-haiku-4-5"));

        var result = await Repo(ctx).GetActiveByTenantAsync(Tenant, CancellationToken.None);

        Assert.NotNull(result);
        // Re-ordered to the catalogue's order on the way out, not the order the column stored.
        Assert.Equal(new[] { "claude-haiku-4-5", "claude-sonnet-5" }, result.ChatEnabledModels);
    }

    /// <summary>
    /// A row whose column was blanked by hand — reachable, since there is no CHECK constraint and the
    /// DBA can write this table directly. It must resolve to the whole catalogue rather than leaving
    /// the tenant with no model, because a chat turn picks the FIRST enabled id and an empty list
    /// would throw on that index.
    /// </summary>
    [Fact]
    public async Task GetActiveByTenant_TreatsABlankedModelColumnAsTheWholeCatalogue()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Config(models: "   "));

        var result = await Repo(ctx).GetActiveByTenantAsync(Tenant, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(ChatModelCatalog.AllIds, result.ChatEnabledModels);
    }

    /// <summary>
    /// The case an active-only lookup would get wrong. An inactive row is invisible to
    /// GetActiveByTenantAsync, but it is still the tenant's ONE row: inserting alongside it would
    /// violate UQ_TenantBillingConfig_Tenant in SQL Server.
    /// </summary>
    [Fact]
    public async Task Upsert_WhenOnlyAnInactiveRowExists_UpdatesItRatherThanInsertingASecond()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Config(planName: "100 análisis", analyses: 100, isActive: false));

        await Repo(ctx).UpsertAsync(Incoming(), CancellationToken.None);

        var rows = await ctx.TenantBillingConfigs.AsNoTracking().Where(x => x.TenantId == Tenant).ToListAsync();
        Assert.Single(rows);
        Assert.Equal("200 análisis", rows[0].PlanName);
        Assert.True(rows[0].IsActive);
    }

    [Fact]
    public async Task Upsert_RepeatedWithTheSameValues_IsIdempotentAndNeverAddsASecondRow()
    {
        using var ctx = NewContext();
        var repo = Repo(ctx);

        await repo.UpsertAsync(Incoming(), CancellationToken.None);
        await repo.UpsertAsync(Incoming(), CancellationToken.None);
        await repo.UpsertAsync(Incoming(), CancellationToken.None);

        var rows = await ctx.TenantBillingConfigs.AsNoTracking().Where(x => x.TenantId == Tenant).ToListAsync();
        Assert.Single(rows);
        Assert.Equal("200 análisis", rows[0].PlanName);
    }

    /// <summary>
    /// Deactivation must keep the commercial history: the row is switched off, never deleted.
    /// </summary>
    [Fact]
    public async Task Upsert_WithIsActiveFalse_DeactivatesTheRowWithoutDeletingIt()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Config(planName: "200 análisis", analyses: 200));

        await Repo(ctx).UpsertAsync(Incoming(isActive: false), CancellationToken.None);

        var rows = await ctx.TenantBillingConfigs.AsNoTracking().Where(x => x.TenantId == Tenant).ToListAsync();
        Assert.Single(rows);
        Assert.False(rows[0].IsActive);
        Assert.Equal("200 análisis", rows[0].PlanName);

        // And it is now invisible to the read, exactly like an absent row.
        Assert.Null(await Repo(ctx).GetActiveByTenantAsync(Tenant, CancellationToken.None));
    }

    /// <summary>
    /// There is no EF global query filter, so this predicate is the only thing stopping one
    /// client's plan write from landing on another's row.
    /// </summary>
    [Fact]
    public async Task Upsert_IsTenantIsolated()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Config(tenantId: Other, planName: "100 análisis", analyses: 100, chatTokens: 4_000_000L));

        await Repo(ctx).UpsertAsync(Incoming(), CancellationToken.None);

        var others = await ctx.TenantBillingConfigs.AsNoTracking().Where(x => x.TenantId == Other).ToListAsync();
        Assert.Single(others);
        Assert.Equal("100 análisis", others[0].PlanName);
        Assert.Equal(100,            others[0].AnalysisMonthlyLimit);

        var mine = await ctx.TenantBillingConfigs.AsNoTracking().Where(x => x.TenantId == Tenant).ToListAsync();
        Assert.Single(mine);
        Assert.Equal("200 análisis", mine[0].PlanName);
    }
}
