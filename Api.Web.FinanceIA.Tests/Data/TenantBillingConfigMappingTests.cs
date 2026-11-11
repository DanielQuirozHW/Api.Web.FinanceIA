// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-31

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Model-metadata tests locking <see cref="TenantBillingConfig"/>'s Fluent mapping to the DDL in
/// docs/database/047_tenant_billing_config.sql, following the <c>FiadorMappingTests</c> precedent.
///
/// Why this exists: the project is Database First, so nothing validates the mapping at build time.
/// A typo in a HasColumnName, a max length that drifts from the column, or a lost index name
/// compiles cleanly and only fails in Azure, at runtime, as "Invalid column name" on the first
/// query that touches the table. Unlike script 046, this delivery DOES ship a query against the
/// new table, so that first failure would be immediate.
///
/// The context is built on the SQL Server provider rather than the in-memory one the repository
/// tests use, because GetTableName/GetColumnName/column types are relational-only concepts — the
/// in-memory provider would report nothing and the assertions would be vacuous. Reading
/// <see cref="DbContext.Model"/> builds the model in process; it never opens a connection, so the
/// bogus connection string is never dialled and no physical table has to exist.
/// </summary>
public sealed class TenantBillingConfigMappingTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────────
    private static IEntityType BillingType()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseSqlServer("Server=none;Database=none;")
            .Options;
        using var context = new FinanciaDbContext(options);
        return context.Model.FindEntityType(typeof(TenantBillingConfig))!;
    }

    private static IProperty Property(string name) => BillingType().FindProperty(name)!;

    // ── Table ───────────────────────────────────────────────────────────────────

    [Fact]
    public void TenantBillingConfig_IsMappedToDboTenantBillingConfig()
    {
        var entityType = BillingType();

        Assert.NotNull(entityType);
        Assert.Equal("TenantBillingConfig", entityType.GetTableName());
        Assert.Equal("dbo", entityType.GetSchema());
    }

    [Fact]
    public void TenantBillingConfig_PrimaryKey_IsBillingConfigId_AndIsDatabaseGenerated()
    {
        // ValueGenerated.OnAdd mirrors DEFAULT (NEWSEQUENTIALID()) in the DDL: the database
        // supplies the key. Marking it otherwise would make EF send its own Guid, or send none.
        var key = BillingType().FindPrimaryKey()!;

        Assert.Equal(nameof(TenantBillingConfig.BillingConfigId), Assert.Single(key.Properties).Name);
        Assert.Equal(ValueGenerated.OnAdd, Property(nameof(TenantBillingConfig.BillingConfigId)).ValueGenerated);
    }

    // ── Columns ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(TenantBillingConfig.BillingConfigId), "BillingConfigId")]
    [InlineData(nameof(TenantBillingConfig.TenantId), "TenantId")]
    [InlineData(nameof(TenantBillingConfig.PlanName), "PlanName")]
    [InlineData(nameof(TenantBillingConfig.AnalysisMonthlyLimit), "AnalysisMonthlyLimit")]
    [InlineData(nameof(TenantBillingConfig.ChatTokensMonthlyLimit), "ChatTokensMonthlyLimit")]
    [InlineData(nameof(TenantBillingConfig.ChatContextBudgetTokens), "ChatContextBudgetTokens")]
    // Property and column names deliberately DIFFER here: the entity exposes both the stored CSV
    // (ChatEnabledModelsRaw) and a parsed view of it (ChatEnabledModels), and the unsuffixed name
    // belongs to the one callers should use. A HasColumnName lost here would send "ChatEnabledModelsRaw".
    [InlineData(nameof(TenantBillingConfig.ChatEnabledModelsRaw), "ChatEnabledModels")]
    [InlineData(nameof(TenantBillingConfig.IsActive), "IsActive")]
    [InlineData(nameof(TenantBillingConfig.UpdatedAt), "UpdatedAt")]
    [InlineData(nameof(TenantBillingConfig.UpdatedBy), "UpdatedBy")]
    public void TenantBillingConfig_Property_MapsToItsDdlColumn(string propertyName, string columnName)
    {
        var property = Property(propertyName);

        Assert.NotNull(property);
        Assert.Equal(columnName, property.GetColumnName());
    }

    [Fact]
    public void TenantBillingConfig_MapsExactlyTheNineDdlColumns()
    {
        // Guards against a property added to the entity without a matching column in 047 or 051 —
        // EF would happily emit it and every query against TenantBillingConfig would break in Azure.
        var mapped = BillingType().GetProperties().Select(p => p.GetColumnName()).OrderBy(n => n);

        Assert.Equal(
            new[]
            {
                "AnalysisMonthlyLimit", "BillingConfigId", "ChatContextBudgetTokens",
                "ChatEnabledModels", "ChatTokensMonthlyLimit", "IsActive", "PlanName", "TenantId",
                "UpdatedAt", "UpdatedBy",
            }.OrderBy(n => n),
            mapped);
    }

    [Theory]
    [InlineData(nameof(TenantBillingConfig.TenantId), 100)]
    [InlineData(nameof(TenantBillingConfig.PlanName), 100)]
    [InlineData(nameof(TenantBillingConfig.UpdatedBy), 100)]
    [InlineData(nameof(TenantBillingConfig.ChatEnabledModelsRaw), 400)]
    public void TenantBillingConfig_StringColumn_HasTheDdlMaxLength(string propertyName, int maxLength)
    {
        Assert.Equal(maxLength, Property(propertyName).GetMaxLength());
    }

    [Fact]
    public void TenantBillingConfig_ParsedModelList_IsNotMapped()
    {
        // ChatEnabledModels is computed from the CSV on every read and has no column of its own. EF
        // would not map a get-only IReadOnlyList<string> by convention, but the Ignore is explicit —
        // and if it were ever dropped, EF would throw at model-build time for a property it cannot
        // map, taking the whole application down at startup rather than failing one query.
        Assert.Null(BillingType().FindProperty(nameof(TenantBillingConfig.ChatEnabledModels)));
    }

    [Theory]
    [InlineData(nameof(TenantBillingConfig.TenantId))]
    [InlineData(nameof(TenantBillingConfig.PlanName))]
    [InlineData(nameof(TenantBillingConfig.AnalysisMonthlyLimit))]
    [InlineData(nameof(TenantBillingConfig.ChatTokensMonthlyLimit))]
    [InlineData(nameof(TenantBillingConfig.ChatContextBudgetTokens))]
    [InlineData(nameof(TenantBillingConfig.ChatEnabledModelsRaw))]
    [InlineData(nameof(TenantBillingConfig.IsActive))]
    [InlineData(nameof(TenantBillingConfig.UpdatedAt))]
    public void TenantBillingConfig_RequiredColumn_IsNotNullable(string propertyName)
    {
        Assert.False(Property(propertyName).IsNullable);
    }

    [Fact]
    public void TenantBillingConfig_UpdatedBy_IsNullable()
    {
        // Nullable because the SuperAdmin write path that would populate it is a later PR; until
        // then the column is set by the DBA or left NULL.
        Assert.True(Property(nameof(TenantBillingConfig.UpdatedBy)).IsNullable);
    }

    [Theory]
    [InlineData(nameof(TenantBillingConfig.TenantId), "nvarchar(100)")]
    [InlineData(nameof(TenantBillingConfig.PlanName), "nvarchar(100)")]
    [InlineData(nameof(TenantBillingConfig.UpdatedBy), "nvarchar(100)")]
    [InlineData(nameof(TenantBillingConfig.ChatEnabledModelsRaw), "nvarchar(400)")]
    [InlineData(nameof(TenantBillingConfig.AnalysisMonthlyLimit), "int")]
    [InlineData(nameof(TenantBillingConfig.ChatContextBudgetTokens), "int")]
    [InlineData(nameof(TenantBillingConfig.IsActive), "bit")]
    [InlineData(nameof(TenantBillingConfig.UpdatedAt), "datetime2")]
    public void TenantBillingConfig_Column_HasTheDdlColumnType(string propertyName, string columnType)
    {
        Assert.Equal(columnType, Property(propertyName).GetColumnType());
    }

    /// <summary>
    /// The headline type decision in script 047: a monthly chat-token pool passes int.MaxValue at
    /// plausible volumes, so the column is BIGINT and the CLR property is long. If either side
    /// narrows to int, this fails here rather than by silently truncating a limit in production.
    /// </summary>
    [Fact]
    public void TenantBillingConfig_ChatTokensMonthlyLimit_IsBigintBackedByLong()
    {
        var property = Property(nameof(TenantBillingConfig.ChatTokensMonthlyLimit));

        Assert.Equal("bigint", property.GetColumnType());
        Assert.Equal(typeof(long), property.ClrType);
    }

    /// <summary>
    /// The mirror-image decision in script 051, and the reason the two columns must not be assumed
    /// to match: a SINGLE conversation's context is bounded by the chat model's 200.000-token
    /// window, five orders of magnitude below int.MaxValue, so this one is INT backed by int.
    /// Widening it to bigint "for consistency" with the pool above would misstate what it is.
    /// </summary>
    [Fact]
    public void TenantBillingConfig_ChatContextBudgetTokens_IsIntBackedByInt()
    {
        var property = Property(nameof(TenantBillingConfig.ChatContextBudgetTokens));

        Assert.Equal("int", property.GetColumnType());
        Assert.Equal(typeof(int), property.ClrType);
    }

    /// <summary>
    /// Script 051 gives the column a DEFAULT (40000) so existing rows backfill, but the mapping
    /// deliberately does NOT declare it — the same choice IsActive makes. Every write goes through
    /// UpsertAsync, which always sets the value; mapping the default here would let an unset
    /// property silently acquire one instead of failing loudly.
    /// </summary>
    [Fact]
    public void TenantBillingConfig_ChatContextBudgetTokens_DoesNotMapTheStoreDefault()
    {
        Assert.Null(Property(nameof(TenantBillingConfig.ChatContextBudgetTokens)).GetDefaultValueSql());
    }

    [Fact]
    public void TenantBillingConfig_ChatEnabledModels_DoesNotMapTheStoreDefault()
    {
        // Same reasoning as the budget above and IsActive below: DF_TenantBillingConfig_ChatEnabledModels
        // exists to backfill rows written before script 052, and every write goes through UpsertAsync,
        // which sets the column explicitly. Mapping the default would let EF omit the column on an
        // INSERT and quietly re-grant the whole catalogue to a tenant a SuperAdmin had just narrowed.
        Assert.Null(Property(nameof(TenantBillingConfig.ChatEnabledModelsRaw)).GetDefaultValueSql());
    }

    [Fact]
    public void TenantBillingConfig_UpdatedAt_DefaultsToSysUtcDateTime()
    {
        // SYSUTCDATETIME(), not GETUTCDATE(): matches script 047 and the TenantSettings precedent
        // for a config table. Both dialects exist in this codebase; the two must not drift apart.
        Assert.Equal("SYSUTCDATETIME()", Property(nameof(TenantBillingConfig.UpdatedAt)).GetDefaultValueSql());
    }

    // ── Relationships ───────────────────────────────────────────────────────────

    [Fact]
    public void TenantBillingConfig_HasNoNavigationProperties()
    {
        // PlanName is a label, not a foreign key, and there is no BillingPlan catalogue table for
        // it to point at — the presets live in code. TenantId is likewise a bare scalar slug.
        Assert.Empty(BillingType().GetNavigations());
        Assert.Empty(BillingType().GetForeignKeys());
    }

    // ── Indexes ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TenantBillingConfig_Tenant_IsUniqueAndUnfiltered()
    {
        // Exactly one configuration row per tenant. Unfiltered on purpose: uniqueness spans
        // inactive rows too, so switching a config off does not free the slot for a second row.
        var index = Assert.Single(
            BillingType().GetIndexes(),
            i => i.GetDatabaseName() == "UQ_TenantBillingConfig_Tenant");

        Assert.True(index.IsUnique);
        Assert.Null(index.GetFilter());
        Assert.Equal(
            new[] { nameof(TenantBillingConfig.TenantId) },
            index.Properties.Select(p => p.Name));
    }

    [Fact]
    public void TenantBillingConfig_HasNoOtherIndexes()
    {
        // The unique constraint on TenantId already serves the only read path, so script 047 adds
        // no further index. A new one here without a matching DDL change would be invisible drift.
        Assert.Equal(1, BillingType().GetIndexes().Count());
    }
}
