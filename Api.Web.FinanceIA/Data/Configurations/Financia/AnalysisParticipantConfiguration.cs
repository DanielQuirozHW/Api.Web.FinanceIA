// Project : Api.Web.FinanceIA
// Created : 2026-10-20

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="AnalysisParticipant"/>.
/// Maps to [dbo].[AnalysisParticipants], created by docs/database/072_person_analyses_and_participants.sql.
/// Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
public sealed class AnalysisParticipantConfiguration : IEntityTypeConfiguration<AnalysisParticipant>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AnalysisParticipant> builder)
    {
        builder.ToTable("AnalysisParticipants", "dbo");

        builder.HasKey(x => x.ParticipantId);

        builder.Property(x => x.ParticipantId)
            .HasColumnName("ParticipantId")
            .ValueGeneratedNever();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.AnalysisId)
            .HasColumnName("AnalysisId")
            .IsRequired();

        builder.Property(x => x.PersonLegajoId)
            .HasColumnName("PersonLegajoId")
            .IsRequired();

        builder.Property(x => x.Role)
            .HasColumnName("Role")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.AmountAtCharge)
            .HasColumnName("AmountAtCharge")
            .HasColumnType("decimal(18,2)")
            .IsRequired();

        builder.Property(x => x.Currency)
            .HasColumnName("Currency")
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(x => x.InstallmentAmount)
            .HasColumnName("InstallmentAmount")
            .HasColumnType("decimal(18,2)");

        builder.Property(x => x.DocumentVersionsJson)
            .HasColumnName("DocumentVersionsJson")
            .HasColumnType("nvarchar(max)")
            .IsRequired();

        builder.Property(x => x.FiltersJson)
            .HasColumnName("FiltersJson")
            .HasColumnType("nvarchar(max)")
            .IsRequired();

        builder.Property(x => x.IndicatorsJson)
            .HasColumnName("IndicatorsJson")
            .HasColumnType("nvarchar(max)")
            .IsRequired();

        builder.Property(x => x.CoverageJson)
            .HasColumnName("CoverageJson")
            .HasColumnType("nvarchar(max)")
            .IsRequired();

        builder.Property(x => x.ScoreJson)
            .HasColumnName("ScoreJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.EvaluationVersion)
            .HasColumnName("EvaluationVersion")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("CreatedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("GETUTCDATE()")
            .IsRequired();

        // DDL 077: the fiador columns, all NULLABLE. Nothing writes them in this release.
        builder.Property(x => x.FiadorId)
            .HasColumnName("FiadorId");

        builder.Property(x => x.GuaranteeType)
            .HasColumnName("GuaranteeType")
            .HasMaxLength(30);

        builder.Property(x => x.SpouseConsent)
            .HasColumnName("SpouseConsent");

        builder.Property(x => x.HousingInopposable)
            .HasColumnName("HousingInopposable");

        builder.Property(x => x.GuaranteesGivenAmount)
            .HasColumnName("GuaranteesGivenAmount")
            .HasColumnType("decimal(18,2)");

        // DDL 078: the solicitante's agro annual mode terms (engine 0.7), both NULLABLE; NULL on a fiador row.
        builder.Property(x => x.CuotasPorAnio)
            .HasColumnName("CuotasPorAnio")
            .HasColumnType("tinyint");

        builder.Property(x => x.AgroModeOverride)
            .HasColumnName("AgroModeOverride");

        // DDL 079: the cuotas vigentes declared by the analyst (engine 0.8), NULLABLE; solicitante and fiador rows.
        builder.Property(x => x.ExistingInstallmentsAmount)
            .HasColumnName("ExistingInstallmentsAmount")
            .HasColumnType("decimal(18,2)");

        builder.HasIndex(x => new { x.TenantId, x.AnalysisId })
            .HasDatabaseName("IX_AnalysisParticipants_TenantId_AnalysisId");

        // ONE row per person and role in an analysis.
        builder.HasIndex(x => new { x.TenantId, x.AnalysisId, x.PersonLegajoId, x.Role })
            .IsUnique()
            .HasDatabaseName("UX_AnalysisParticipants_Analysis_Legajo_Role");

        // FK_AnalysisParticipants_Analyses, FK_AnalysisParticipants_PersonLegajos and (DDL 077)
        // FK_AnalysisParticipants_Fiadores exist in the database but are deliberately NOT modelled, as in
        // 070/071. PersonAnalysisRepository.CreateAsync inserts the
        // Analyses row in its own SaveChanges before the result and the participant, inside one transaction,
        // so the order never depends on EF sorting unrelated commands.
    }
}
