// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-09

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Query-level tests for <see cref="TenantSettingsRepository"/>, run against the EF Core in-memory
/// provider so the actual WHERE clause is exercised rather than a stub's say-so.
///
/// <para>
/// The write path is what this file mostly exists for. <c>UQ_TenantSettings_Tenant_Key</c> allows
/// exactly one row per (tenant, key), so the second write of a key must UPDATE — a blind INSERT
/// would be rejected by the constraint in Azure SQL. The in-memory provider does NOT enforce that
/// unique index, so a duplicate here would pass silently; the tests therefore assert the ROW COUNT
/// explicitly rather than trusting the provider to complain.
/// </para>
///
/// <para>
/// TENANT ISOLATION IS PROVEN HERE, not in the service tests, which stub this repository away. The
/// project has no EF global query filter, so the <c>TenantId</c> predicate written in this
/// repository is the only thing keeping one tenant's settings out of another's.
/// </para>
/// </summary>
public sealed class TenantSettingsRepositoryTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static FinanciaDbContext NewContext(string name)
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new FinanciaDbContext(options);
    }

    private static TenantSettingsRepository Sut(FinanciaDbContext ctx)
        => new(ctx, NullLogger<TenantSettingsRepository>.Instance);

    private static async Task SeedAsync(FinanciaDbContext ctx, params TenantSettings[] rows)
    {
        ctx.TenantSettings.AddRange(rows);
        ctx.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await ctx.SaveChangesAsync();
    }

    private static TenantSettings Row(
        string tenantId = Tenant,
        string key = TenantSettingKeys.EntityType,
        string value = TenantSettingKeys.EntityTypeSgr,
        string? description = null,
        DateTime? updatedAt = null) => new()
    {
        SettingId    = Guid.NewGuid(),
        TenantId     = tenantId,
        SettingKey   = key,
        SettingValue = value,
        Description  = description,
        UpdatedAt    = updatedAt ?? DateTime.UtcNow.AddDays(-30)
    };

    // ── Upsert: insert then update ──────────────────────────────────────────────

    [Fact]
    public async Task UpsertAsync_WhenNoRowExists_InsertsOne()
    {
        var db = $"settings-{Guid.NewGuid()}";
        await using var ctx = NewContext(db);

        await Sut(ctx).UpsertAsync(Tenant, TenantSettingKeys.EntityType,
            TenantSettingKeys.EntityTypeNonSgr, CancellationToken.None);

        await using var verify = NewContext(db);
        var row = Assert.Single(verify.TenantSettings);
        Assert.Equal(Tenant, row.TenantId);
        Assert.Equal(TenantSettingKeys.EntityType, row.SettingKey);
        Assert.Equal(TenantSettingKeys.EntityTypeNonSgr, row.SettingValue);
    }

    /// <summary>
    /// The constraint test. A second write of the same key must leave ONE row, not two — the
    /// in-memory provider would happily keep a duplicate that Azure SQL rejects.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_WhenRowExists_UpdatesItAndDoesNotInsertASecond()
    {
        var db = $"settings-{Guid.NewGuid()}";
        await using (var seed = NewContext(db))
        {
            await SeedAsync(seed, Row(value: TenantSettingKeys.EntityTypeSgr));
        }

        await using (var act = NewContext(db))
        {
            await Sut(act).UpsertAsync(Tenant, TenantSettingKeys.EntityType,
                TenantSettingKeys.EntityTypeNonSgr, CancellationToken.None);
        }

        await using var verify = NewContext(db);
        var row = Assert.Single(verify.TenantSettings);
        Assert.Equal(TenantSettingKeys.EntityTypeNonSgr, row.SettingValue);
    }

    /// <summary>
    /// DF_TenantSettings_UpdatedAt fires on INSERT only, so an UPDATE that did not assign the
    /// column would silently keep the row's original timestamp.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_OnUpdate_MovesUpdatedAt()
    {
        var db = $"settings-{Guid.NewGuid()}";
        var stale = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await using (var seed = NewContext(db))
        {
            await SeedAsync(seed, Row(updatedAt: stale));
        }

        await using (var act = NewContext(db))
        {
            await Sut(act).UpsertAsync(Tenant, TenantSettingKeys.EntityType,
                TenantSettingKeys.EntityTypeNonSgr, CancellationToken.None);
        }

        await using var verify = NewContext(db);
        Assert.True(Assert.Single(verify.TenantSettings).UpdatedAt > stale);
    }

    /// <summary>
    /// Description carries the DBA's Spanish prose (see docs/guides/tenant-profile.md). An edit
    /// through the API must not blank it.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_OnUpdate_PreservesTheOperatorDescription()
    {
        var db = $"settings-{Guid.NewGuid()}";
        const string Prose = "Entidad no-SGR: aplica el preset non_sgr (CASFOG deshabilitado).";

        await using (var seed = NewContext(db))
        {
            await SeedAsync(seed, Row(description: Prose));
        }

        await using (var act = NewContext(db))
        {
            await Sut(act).UpsertAsync(Tenant, TenantSettingKeys.EntityType,
                TenantSettingKeys.EntityTypeNonSgr, CancellationToken.None);
        }

        await using var verify = NewContext(db);
        Assert.Equal(Prose, Assert.Single(verify.TenantSettings).Description);
    }

    // ── Tenant isolation ────────────────────────────────────────────────────────

    /// <summary>
    /// Another tenant's row for the SAME key must be invisible to the write: without the TenantId
    /// predicate the upsert would find it and overwrite that tenant's profile.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_DoesNotTouchAnotherTenantsRowForTheSameKey()
    {
        var db = $"settings-{Guid.NewGuid()}";
        await using (var seed = NewContext(db))
        {
            await SeedAsync(seed, Row(tenantId: Other, value: TenantSettingKeys.EntityTypeSgr));
        }

        await using (var act = NewContext(db))
        {
            await Sut(act).UpsertAsync(Tenant, TenantSettingKeys.EntityType,
                TenantSettingKeys.EntityTypeNonSgr, CancellationToken.None);
        }

        await using var verify = NewContext(db);
        Assert.Equal(2, await verify.TenantSettings.CountAsync());

        var other = await verify.TenantSettings.SingleAsync(s => s.TenantId == Other);
        Assert.Equal(TenantSettingKeys.EntityTypeSgr, other.SettingValue);
    }

    /// <summary>A different key for the same tenant is a different row, not an overwrite.</summary>
    [Fact]
    public async Task UpsertAsync_DoesNotTouchADifferentKeyForTheSameTenant()
    {
        var db = $"settings-{Guid.NewGuid()}";
        await using (var seed = NewContext(db))
        {
            await SeedAsync(seed, Row(key: TenantSettingKeys.CreditLineMode,
                                      value: TenantSettingKeys.CreditLineModeSimple));
        }

        await using (var act = NewContext(db))
        {
            await Sut(act).UpsertAsync(Tenant, TenantSettingKeys.EntityType,
                TenantSettingKeys.EntityTypeNonSgr, CancellationToken.None);
        }

        await using var verify = NewContext(db);
        Assert.Equal(2, await verify.TenantSettings.CountAsync());

        var mode = await verify.TenantSettings
            .SingleAsync(s => s.SettingKey == TenantSettingKeys.CreditLineMode);
        Assert.Equal(TenantSettingKeys.CreditLineModeSimple, mode.SettingValue);
    }

    // ── Reads still honour the tenant ───────────────────────────────────────────

    [Fact]
    public async Task GetValueAsync_DoesNotReadAnotherTenantsRow()
    {
        var db = $"settings-{Guid.NewGuid()}";
        await using var ctx = NewContext(db);
        await SeedAsync(ctx, Row(tenantId: Other, value: TenantSettingKeys.EntityTypeNonSgr));

        var value = await Sut(ctx).GetValueAsync(Tenant, TenantSettingKeys.EntityType, CancellationToken.None);

        Assert.Null(value);
    }

    [Fact]
    public async Task UpsertAsync_IsVisibleToASubsequentRead()
    {
        var db = $"settings-{Guid.NewGuid()}";
        await using var ctx = NewContext(db);

        await Sut(ctx).UpsertAsync(Tenant, TenantSettingKeys.EntityType,
            TenantSettingKeys.EntityTypeNonSgr, CancellationToken.None);

        var values = await Sut(ctx).GetValuesAsync(
            Tenant, [TenantSettingKeys.EntityType], CancellationToken.None);

        Assert.Equal(TenantSettingKeys.EntityTypeNonSgr, values[TenantSettingKeys.EntityType]);
    }
}
