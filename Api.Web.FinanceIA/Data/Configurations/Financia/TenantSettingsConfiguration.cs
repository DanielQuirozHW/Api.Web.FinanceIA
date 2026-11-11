// Project : Api.Web.FinanceIA
// Created : 2026-08-03

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="TenantSettings"/>.
/// Maps to [dbo].[TenantSettings]. Database First — schema changes are applied via
/// DDL scripts, not migrations.
/// </summary>
public sealed class TenantSettingsConfiguration : IEntityTypeConfiguration<TenantSettings>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TenantSettings> builder)
    {
        builder.ToTable("TenantSettings", "dbo");

        builder.HasKey(x => x.SettingId);

        builder.Property(x => x.SettingId)
            .HasColumnName("SettingId")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.SettingKey)
            .HasColumnName("SettingKey")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.SettingValue)
            .HasColumnName("SettingValue")
            .HasColumnType("nvarchar(max)")
            .IsRequired();

        builder.Property(x => x.Description)
            .HasColumnName("Description")
            .HasMaxLength(500);

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("UpdatedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .IsRequired();

        builder.HasIndex(x => new { x.TenantId, x.SettingKey })
            .IsUnique()
            .HasDatabaseName("UQ_TenantSettings_Tenant_Key");
    }
}
