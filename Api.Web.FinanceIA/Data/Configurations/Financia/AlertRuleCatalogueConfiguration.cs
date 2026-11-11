// Project : Api.Web.FinanceIA
// Created : 2026-08-16

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="AlertRuleDefinition"/>.
/// Maps to [dbo].[AlertRuleCatalogue] in the per-tenant business database.
/// Database First — schema changes are applied via DDL scripts (038), not migrations.
/// </summary>
public sealed class AlertRuleCatalogueConfiguration : IEntityTypeConfiguration<AlertRuleDefinition>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AlertRuleDefinition> builder)
    {
        builder.ToTable("AlertRuleCatalogue", "dbo");

        builder.HasKey(x => x.RuleId);

        builder.Property(x => x.RuleId)
            .HasColumnName("RuleId")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        // NVARCHAR(50) so the value also fits [dbo].[Alerts].[AlertType] unchanged.
        builder.Property(x => x.Code)
            .HasColumnName("Code")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.Enabled)
            .HasColumnName("Enabled")
            .IsRequired();

        builder.Property(x => x.Severity)
            .HasColumnName("Severity")
            .HasMaxLength(20)
            .IsRequired();

        // Nullable: a rule either has a threshold or it does not. Never zero-as-absent.
        builder.Property(x => x.ThresholdValue)
            .HasColumnName("ThresholdValue")
            .HasColumnType("decimal(10,4)");

        // NVARCHAR(300) so the value also fits [dbo].[Alerts].[Title] unchanged.
        builder.Property(x => x.DisplayNameEs)
            .HasColumnName("DisplayNameEs")
            .HasMaxLength(300)
            .IsRequired();

        builder.Property(x => x.DescriptionTemplateEs)
            .HasColumnName("DescriptionTemplateEs")
            .HasMaxLength(1000)
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("CreatedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("UpdatedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .IsRequired();

        builder.Property(x => x.UpdatedBy)
            .HasColumnName("UpdatedBy");

        builder.HasIndex(x => new { x.TenantId, x.Code })
            .IsUnique()
            .HasDatabaseName("UQ_AlertRuleCatalogue_Tenant_Code");

        builder.HasIndex(x => new { x.TenantId, x.Enabled })
            .HasDatabaseName("IX_AlertRuleCatalogue_TenantId_Enabled");
    }
}
