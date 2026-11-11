// Project : Api.Web.FinanceIA
// Created : 2026-05-10

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="Alert"/>.
/// Maps to [dbo].[Alerts] in the per-tenant business database.
/// Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
public sealed class AlertConfiguration : IEntityTypeConfiguration<Alert>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Alert> builder)
    {
        builder.ToTable("Alerts", "dbo");

        builder.HasKey(x => x.AlertId);

        // AlertId is a plain uniqueidentifier (no IDENTITY, no DEFAULT) — the
        // application supplies the Guid on insert, mirroring Analysis/AnalysisResult.
        // Do NOT mark it ValueGeneratedOnAdd: that tells EF the DB generates the key
        // and makes EF omit the explicit Guid, which breaks the INSERT silently.
        builder.Property(x => x.AlertId)
            .HasColumnName("AlertId")
            .ValueGeneratedNever();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasColumnType("nvarchar(100)")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.EntityId)
            .HasColumnName("EntityId");

        builder.Property(x => x.AnalysisId)
            .HasColumnName("AnalysisId");

        builder.Property(x => x.AlertType)
            .HasColumnName("AlertType")
            .HasColumnType("nvarchar(50)")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.Severity)
            .HasColumnName("Severity")
            .HasColumnType("nvarchar(20)")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.Title)
            .HasColumnName("Title")
            .HasColumnType("nvarchar(300)")
            .HasMaxLength(300)
            .IsRequired();

        builder.Property(x => x.Description)
            .HasColumnName("Description")
            .HasColumnType("nvarchar(max)")
            .IsRequired();

        builder.Property(x => x.MetricKey)
            .HasColumnName("MetricKey")
            .HasColumnType("nvarchar(50)")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.MetricValue)
            .HasColumnName("MetricValue")
            .HasColumnType("decimal(10,4)")
            .IsRequired();

        builder.Property(x => x.ThresholdValue)
            .HasColumnName("ThresholdValue")
            .HasColumnType("decimal(10,4)")
            .IsRequired();

        builder.Property(x => x.IsRead)
            .HasColumnName("IsRead")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(x => x.ReadBy)
            .HasColumnName("ReadBy");

        builder.Property(x => x.ReadAt)
            .HasColumnName("ReadAt")
            .HasColumnType("datetime2");

        builder.Property(x => x.IsResolved)
            .HasColumnName("IsResolved")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(x => x.ResolvedBy)
            .HasColumnName("ResolvedBy");

        builder.Property(x => x.ResolvedAt)
            .HasColumnName("ResolvedAt")
            .HasColumnType("datetime2");

        builder.Property(x => x.CreatedAt)
            .HasColumnName("CreatedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("GETUTCDATE()")
            .IsRequired();

        builder.HasIndex(x => x.TenantId)
            .HasDatabaseName("IX_Alerts_TenantId");
    }
}
