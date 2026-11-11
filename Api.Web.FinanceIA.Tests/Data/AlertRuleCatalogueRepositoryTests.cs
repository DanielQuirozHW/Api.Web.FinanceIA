// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-16

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Query-level tests for the read paths of <see cref="AlertRuleCatalogueRepository"/>, run against
/// the EF Core in-memory provider so the actual WHERE clauses and ordering are exercised.
///
/// UpdateAsync IS NOT COVERED HERE. It uses ExecuteUpdateAsync, which the in-memory provider does
/// not implement — the same reason CreditLineRepositoryTests covers neither RaiseAsync nor
/// SetStatusAsync. Its column scope is verified by review of the single SetProperty chain and its
/// call-side behaviour by AlertRuleCatalogueControllerTests; a test here would only prove that a
/// stub returns what it was handed.
/// </summary>
public sealed class AlertRuleCatalogueRepositoryTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static FinanciaDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"alertrules-{Guid.NewGuid()}")
            .Options;
        return new FinanciaDbContext(options);
    }

    private static AlertRuleDefinition Rule(
        string code, bool enabled = false, string tenantId = Tenant) => new()
    {
        RuleId                = Guid.NewGuid(),
        TenantId              = tenantId,
        Code                  = code,
        Enabled               = enabled,
        Severity              = "medium",
        ThresholdValue        = 0.5000m,
        DisplayNameEs         = "Regla",
        DescriptionTemplateEs = "Descripción",
        CreatedAt             = new DateTime(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt             = new DateTime(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc),
    };

    private static async Task SeedAsync(FinanciaDbContext ctx, params AlertRuleDefinition[] rules)
    {
        ctx.AlertRuleCatalogue.AddRange(rules);
        ctx.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await ctx.SaveChangesAsync();
    }

    private static AlertRuleCatalogueRepository Repo(FinanciaDbContext ctx)
        => new(ctx, NullLogger<AlertRuleCatalogueRepository>.Instance);

    // ── GetAllAsync ─────────────────────────────────────────────────────────────

    /// <summary>
    /// THE POINT: every seeded rule ships disabled, so a read that filtered on Enabled — as
    /// GetEnabledAsync correctly does for the engine — would return nothing at all to the
    /// configuration screen whose entire purpose is enabling them.
    /// </summary>
    [Fact]
    public async Task GetAll_ReturnsDisabledRules()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Rule(AlertRuleEngine.InventarioElevado,   enabled: false),
            Rule(AlertRuleEngine.PasivoCienCorriente, enabled: true));

        var rows = await Repo(ctx).GetAllAsync(Tenant, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => !r.Enabled);
    }

    [Fact]
    public async Task GetAll_ExcludesOtherTenants()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Rule(AlertRuleEngine.InventarioElevado),
            Rule(AlertRuleEngine.InventarioElevado, tenantId: Other));

        var rows = await Repo(ctx).GetAllAsync(Tenant, CancellationToken.None);

        var only = Assert.Single(rows);
        Assert.Equal(Tenant, only.TenantId);
    }

    /// <summary>
    /// Ordinal ordering, not the database's collation ordering: the screen's row order must not
    /// depend on the query plan. Same reasoning as AlertRuleEngine.Evaluate's OrderBy.
    /// </summary>
    [Fact]
    public async Task GetAll_OrdersByCodeOrdinal()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Rule(AlertRuleEngine.InventarioElevado),
            Rule(AlertRuleEngine.CoberturaInteresesIndeterminable),
            Rule(AlertRuleEngine.FlujoFinanciacionCero));

        var rows = await Repo(ctx).GetAllAsync(Tenant, CancellationToken.None);

        Assert.Equal(
            rows.Select(r => r.Code).OrderBy(c => c, StringComparer.Ordinal).ToArray(),
            rows.Select(r => r.Code).ToArray());
    }

    [Fact]
    public async Task GetAll_WhenTenantHasNoRules_ReturnsEmpty()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx, Rule(AlertRuleEngine.InventarioElevado, tenantId: Other));

        var rows = await Repo(ctx).GetAllAsync(Tenant, CancellationToken.None);

        Assert.Empty(rows);
    }

    // ── GetByIdAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetById_ReturnsRuleForOwningTenant()
    {
        var rule = Rule(AlertRuleEngine.InventarioElevado);
        using var ctx = NewContext();
        await SeedAsync(ctx, rule);

        var found = await Repo(ctx).GetByIdAsync(rule.RuleId, Tenant, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(rule.RuleId, found!.RuleId);
    }

    /// <summary>
    /// THE CROSS-TENANT GUARD: another tenant's rule id must be indistinguishable from a missing
    /// one, so the controller 404s instead of ever writing across the boundary.
    /// </summary>
    [Fact]
    public async Task GetById_WhenRuleBelongsToAnotherTenant_ReturnsNull()
    {
        var foreign = Rule(AlertRuleEngine.InventarioElevado, tenantId: Other);
        using var ctx = NewContext();
        await SeedAsync(ctx, foreign);

        var found = await Repo(ctx).GetByIdAsync(foreign.RuleId, Tenant, CancellationToken.None);

        Assert.Null(found);
    }

    // ── GetEnabledAsync is untouched by this change ─────────────────────────────

    /// <summary>
    /// Regression guard: the engine's read must keep filtering on Enabled even though a sibling
    /// method that deliberately does not now sits beside it.
    /// </summary>
    [Fact]
    public async Task GetEnabled_StillReturnsOnlyEnabledRules()
    {
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Rule(AlertRuleEngine.InventarioElevado,   enabled: false),
            Rule(AlertRuleEngine.PasivoCienCorriente, enabled: true));

        var rows = await Repo(ctx).GetEnabledAsync(Tenant, CancellationToken.None);

        var only = Assert.Single(rows);
        Assert.Equal(AlertRuleEngine.PasivoCienCorriente, only.Code);
    }
}
