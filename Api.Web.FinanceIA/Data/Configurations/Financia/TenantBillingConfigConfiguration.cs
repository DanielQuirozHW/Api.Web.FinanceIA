// Project : Api.Web.FinanceIA
// Created : 2026-08-31

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="TenantBillingConfig"/>.
/// Maps to [dbo].[TenantBillingConfig] in the per-tenant business database.
/// Database First — schema changes are applied via DDL scripts, not migrations
/// (docs/database/047_tenant_billing_config.sql).
/// </summary>
public sealed class TenantBillingConfigConfiguration : IEntityTypeConfiguration<TenantBillingConfig>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TenantBillingConfig> builder)
    {
        builder.ToTable("TenantBillingConfig", "dbo");

        builder.HasKey(x => x.BillingConfigId);

        builder.Property(x => x.BillingConfigId)
            .HasColumnName("BillingConfigId")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.PlanName)
            .HasColumnName("PlanName")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.AnalysisMonthlyLimit)
            .HasColumnName("AnalysisMonthlyLimit")
            .IsRequired();

        // bigint, not int: a monthly chat-token pool passes int.MaxValue at plausible volumes.
        builder.Property(x => x.ChatTokensMonthlyLimit)
            .HasColumnName("ChatTokensMonthlyLimit")
            .HasColumnType("bigint")
            .IsRequired();

        // int, not bigint, and deliberately unlike the column above: this bounds ONE conversation's
        // context, whose ceiling is the model's 200.000-token window — nowhere near int.MaxValue.
        //
        // The column's DEFAULT (40000) is NOT mapped either, for the same reason as IsActive below:
        // the store default exists to backfill rows that predate script 051, and every write goes
        // through TenantBillingConfigRepository.UpsertAsync, which always sets this explicitly.
        builder.Property(x => x.ChatContextBudgetTokens)
            .HasColumnName("ChatContextBudgetTokens")
            .IsRequired();

        // The property is ChatEnabledModelsRaw, the column is ChatEnabledModels: the entity exposes
        // BOTH the stored CSV and a parsed view of it, and the unsuffixed name belongs to the one
        // callers should use. NVARCHAR(400) matches script 052 — an over-long list would otherwise
        // fail the write as a 500 truncation rather than as validation.
        //
        // The column's DEFAULT is NOT mapped, for the same reason as ChatContextBudgetTokens above
        // and IsActive below: it exists to backfill rows that predate script 052, and every write
        // goes through TenantBillingConfigRepository.UpsertAsync, which always sets this explicitly.
        builder.Property(x => x.ChatEnabledModelsRaw)
            .HasColumnName("ChatEnabledModels")
            .HasMaxLength(400)
            .IsRequired();

        // The parsed view is computed on every read and has no column of its own. EF would not map a
        // get-only IReadOnlyList<string> by convention anyway; saying so explicitly is the project's
        // documented habit for anything on an entity that is not a column.
        builder.Ignore(x => x.ChatEnabledModels);

        // The column's DEFAULT (1) is deliberately NOT mapped, matching how FiadorConfiguration
        // leaves DF_Fiadores_Origen and DF_Fiadores_Status to the database. Note for the PR that
        // adds a write path: bool's CLR default is false, so EF sends 0 for an unset IsActive and
        // the database default never applies — an inserted row must set it explicitly.
        builder.Property(x => x.IsActive)
            .HasColumnName("IsActive")
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("UpdatedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .IsRequired();

        builder.Property(x => x.UpdatedBy)
            .HasColumnName("UpdatedBy")
            .HasMaxLength(100);

        // Exactly one configuration row per tenant.
        builder.HasIndex(x => x.TenantId)
            .IsUnique()
            .HasDatabaseName("UQ_TenantBillingConfig_Tenant");
    }
}
