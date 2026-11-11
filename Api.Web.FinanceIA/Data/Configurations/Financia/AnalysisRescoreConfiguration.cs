// Project : Api.Web.FinanceIA
// Created : 2026-10-22

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="AnalysisRescore"/>.
/// Maps [dbo].[AnalysisRescores] (DDL 073).
/// Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
/// <remarks>
/// Mapping it brings DDL 073 under the <c>--verify-schema</c> deploy gate, which reads the EF model.
/// </remarks>
public sealed class AnalysisRescoreConfiguration : IEntityTypeConfiguration<AnalysisRescore>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AnalysisRescore> builder)
    {
        builder.ToTable("AnalysisRescores", "dbo");

        builder.HasKey(x => x.RescoreId);

        // The application supplies the Guid on insert (ValueGeneratedNever), as for the votes table.
        builder.Property(x => x.RescoreId)
            .HasColumnName("RescoreId")
            .ValueGeneratedNever();

        // nvarchar(100) Firebase slug, like every business table since DDL 005.
        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.AnalysisId)
            .HasColumnName("AnalysisId")
            .IsRequired();

        builder.Property(x => x.ResultId)
            .HasColumnName("ResultId")
            .IsRequired();

        builder.Property(x => x.RescoredAt)
            .HasColumnName("RescoredAt")
            .HasColumnType("datetime2")
            .IsRequired();

        builder.Property(x => x.RescoredBy)
            .HasColumnName("RescoredBy")
            .IsRequired();

        // Same type as AnalysisResults.AiSuggestedScore / AiSuggestedLevel / EnrichmentStatus.
        builder.Property(x => x.PreviousScore)
            .HasColumnName("PreviousScore")
            .HasColumnType("decimal(5,2)");

        builder.Property(x => x.NewScore)
            .HasColumnName("NewScore")
            .HasColumnType("decimal(5,2)");

        builder.Property(x => x.PreviousLevel)
            .HasColumnName("PreviousLevel")
            .HasMaxLength(20);

        builder.Property(x => x.NewLevel)
            .HasColumnName("NewLevel")
            .HasMaxLength(20);

        builder.Property(x => x.PreviousEnrichmentStatus)
            .HasColumnName("PreviousEnrichmentStatus")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.NewEnrichmentStatus)
            .HasColumnName("NewEnrichmentStatus")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.PreviousValuesJson)
            .HasColumnName("PreviousValuesJson")
            .HasColumnType("nvarchar(max)")
            .IsRequired();

        builder.Property(x => x.SourcesJson)
            .HasColumnName("SourcesJson")
            .HasColumnType("nvarchar(max)")
            .IsRequired();

        // Serves the history read (newest first) and the detail's "latest sources" lookup. The name
        // and the DESC order match DDL 073 exactly. No HasOne: this context models no relationships;
        // FK_AnalysisRescores_AnalysisId exists in the database only.
        builder.HasIndex(x => new { x.TenantId, x.AnalysisId, x.RescoredAt })
            .IsDescending(false, false, true)
            .HasDatabaseName("IX_AnalysisRescores_TenantId_AnalysisId_RescoredAt");
    }
}
