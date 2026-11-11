// Project : Api.Web.FinanceIA
// Created : 2026-05-08

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="ScoringConfig"/>.
/// Maps to [dbo].[ScoringConfig] in the per-tenant business database.
/// Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
public sealed class ScoringConfigConfiguration : IEntityTypeConfiguration<ScoringConfig>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ScoringConfig> builder)
    {
        builder.ToTable("ScoringConfig", "dbo");

        builder.HasKey(x => x.ConfigId);

        builder.Property(x => x.ConfigId)
            .HasColumnName("ConfigId")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.ConfigKey)
            .HasColumnName("ConfigKey")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.ConfigValue)
            .HasColumnName("ConfigValue")
            .HasColumnType("decimal(10,4)")
            .IsRequired();

        builder.Property(x => x.Description)
            .HasColumnName("Description")
            .HasMaxLength(500);

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("UpdatedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("GETUTCDATE()")
            .IsRequired();

        builder.HasIndex(x => new { x.TenantId, x.ConfigKey })
            .IsUnique()
            .HasDatabaseName("UQ_ScoringConfig_Tenant_Key");
    }
}
