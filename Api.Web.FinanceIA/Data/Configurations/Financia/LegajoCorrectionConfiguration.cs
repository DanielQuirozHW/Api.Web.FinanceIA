// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="LegajoCorrection"/>.
/// Maps to [dbo].[LegajoCorrections], created by docs/database/070_legajo_documents_and_extractions.sql.
/// Append-only. Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
public sealed class LegajoCorrectionConfiguration : IEntityTypeConfiguration<LegajoCorrection>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<LegajoCorrection> builder)
    {
        builder.ToTable("LegajoCorrections", "dbo");

        builder.HasKey(x => x.CorrectionId);

        builder.Property(x => x.CorrectionId)
            .HasColumnName("CorrectionId")
            .ValueGeneratedNever();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.ExtractionId)
            .HasColumnName("ExtractionId")
            .IsRequired();

        builder.Property(x => x.FieldPath)
            .HasColumnName("FieldPath")
            .HasMaxLength(300)
            .IsRequired();

        builder.Property(x => x.OriginalValueJson)
            .HasColumnName("OriginalValueJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.CorrectedValueJson)
            .HasColumnName("CorrectedValueJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.Reason)
            .HasColumnName("Reason")
            .HasMaxLength(1000)
            .IsRequired();

        builder.Property(x => x.CorrectedBy)
            .HasColumnName("CorrectedBy")
            .IsRequired();

        builder.Property(x => x.CorrectedAt)
            .HasColumnName("CorrectedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("GETUTCDATE()")
            .IsRequired();

        builder.HasIndex(x => x.ExtractionId)
            .HasDatabaseName("IX_LegajoCorrections_ExtractionId");

        // FK_LegajoCorrections_LegajoExtractions exists in the database but is deliberately NOT
        // modelled: a correction is always written against a run saved earlier. ⚠ A future write that
        // inserts a run and a correction to it in the SAME SaveChanges must model that FK first — the
        // #237 lesson recorded on EntityConfiguration.
    }
}
