// Project : Api.Web.FinanceIA
// Created : 2026-05-09

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="ExportLog"/>.
/// Maps to [dbo].[ExportLogs] in the per-tenant business database.
/// Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
public sealed class ExportLogConfiguration : IEntityTypeConfiguration<ExportLog>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ExportLog> builder)
    {
        builder.ToTable("ExportLogs", "dbo");

        builder.HasKey(x => x.ExportLogId);

        builder.Property(x => x.ExportLogId)
            .HasColumnName("ExportLogId")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.AnalysisId)
            .HasColumnName("AnalysisId")
            .IsRequired();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.ExportedBy)
            .HasColumnName("ExportedBy")
            .IsRequired();

        builder.Property(x => x.Format)
            .HasColumnName("Format")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.SectionsIncluded)
            .HasColumnName("SectionsIncluded")
            .HasColumnType("nvarchar(max)")
            .IsRequired();

        builder.Property(x => x.FileHashSha256)
            .HasColumnName("FileHashSha256")
            .HasColumnType("nchar(64)")
            .IsRequired();

        builder.Property(x => x.IpAddress)
            .HasColumnName("IpAddress")
            .HasMaxLength(50);

        builder.Property(x => x.Template)
            .HasColumnName("Template")
            .HasMaxLength(20);

        builder.Property(x => x.ExportedAt)
            .HasColumnName("ExportedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("GETUTCDATE()")
            .IsRequired();

        builder.HasIndex(x => new { x.TenantId, x.ExportedAt })
            .HasDatabaseName("IX_ExportLogs_TenantId_ExportedAt");

        builder.HasIndex(x => x.AnalysisId)
            .HasDatabaseName("IX_ExportLogs_AnalysisId");
    }
}
