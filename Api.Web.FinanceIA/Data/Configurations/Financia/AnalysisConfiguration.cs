// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-10-20

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="Analysis"/>.
/// Maps [dbo].[Analyses]. Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
public sealed class AnalysisConfiguration : IEntityTypeConfiguration<Analysis>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Analysis> builder)
    {
        builder.ToTable("Analyses", "dbo");

        builder.HasKey(x => x.AnalysisId);

        builder.Property(x => x.AnalysisId)
            .HasColumnName("AnalysisId")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.EntityId)
            .HasColumnName("EntityId")
            .IsRequired();

        // NULLABLE since DDL 072, and only on a persona humana analysis (CK_Analyses_DocumentId_Kind).
        builder.Property(x => x.DocumentId)
            .HasColumnName("DocumentId");

        // DDL 072. NOT NULL with DEFAULT N'Balance' in the database; the entity initialises it too, so
        // every INSERT names the kind explicitly and the default only covers rows written by older code.
        builder.Property(x => x.AnalysisKind)
            .HasColumnName("AnalysisKind")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.RequestedBy)
            .HasColumnName("RequestedBy")
            .IsRequired();

        builder.Property(x => x.RequestedAmount)
            .HasColumnName("RequestedAmount")
            .HasColumnType("decimal(18, 2)");

        builder.Property(x => x.Status)
            .HasColumnName("Status")
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(x => x.FiscalYear)
            .HasColumnName("FiscalYear")
            .IsRequired();

        builder.Property(x => x.ComparativeAnalysisId)
            .HasColumnName("ComparativeAnalysisId");

        builder.Property(x => x.AiModel)
            .HasColumnName("AiModel")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.AiTemperature)
            .HasColumnName("AiTemperature")
            .HasColumnType("decimal(3, 2)")
            .IsRequired();

        builder.Property(x => x.PromptVersion)
            .HasColumnName("PromptVersion")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.ProcessingStartedAt)
            .HasColumnName("ProcessingStartedAt");

        builder.Property(x => x.ProcessingCompletedAt)
            .HasColumnName("ProcessingCompletedAt");

        builder.Property(x => x.ProcessingDurationMs)
            .HasColumnName("ProcessingDurationMs");

        // ── Pre-approval record (DDL 058) ─────────────────────────────────────
        // All nullable, mapped in the same bare style as ReviewedBy/ReviewedAt below.
        builder.Property(x => x.PreApprovedBy)
            .HasColumnName("PreApprovedBy");

        builder.Property(x => x.PreApprovedAt)
            .HasColumnName("PreApprovedAt");

        builder.Property(x => x.PreApprovedAmount)
            .HasColumnName("PreApprovedAmount")
            .HasColumnType("decimal(18, 2)");

        builder.Property(x => x.PreApprovedCurrency)
            .HasColumnName("PreApprovedCurrency")
            .HasMaxLength(3);

        builder.Property(x => x.ReviewedBy)
            .HasColumnName("ReviewedBy");

        builder.Property(x => x.ReviewedAt)
            .HasColumnName("ReviewedAt");

        builder.Property(x => x.ReviewNotes)
            .HasColumnName("ReviewNotes");

        // ── Final decision amount (DDL 058) ───────────────────────────────────
        // decimal(18, 2) matches RequestedAmount and CreditLines.Amount, so a decision amount
        // round-trips into a cupo without a precision change.
        builder.Property(x => x.DecisionAmount)
            .HasColumnName("DecisionAmount")
            .HasColumnType("decimal(18, 2)");

        builder.Property(x => x.DecisionCurrency)
            .HasColumnName("DecisionCurrency")
            .HasMaxLength(3);

        // ── Operations behind each record (DDL 061) ───────────────────────────
        // nvarchar(max) JSON blobs, mapped in the same bare style as AnalysisResults.BcraDataJson
        // and Entities.LufeDataJson. Mapping them makes EF emit both columns in every SELECT and
        // UPDATE for Analysis, so 061 MUST be applied before this code is deployed.
        builder.Property(x => x.ProposedOperationsJson)
            .HasColumnName("ProposedOperationsJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.ConfirmedOperationsJson)
            .HasColumnName("ConfirmedOperationsJson")
            .HasColumnType("nvarchar(max)");

        // ── Confirmations behind each record (DDL 072) ────────────────────────
        builder.Property(x => x.PreApprovalChecksJson)
            .HasColumnName("PreApprovalChecksJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.ConfirmationChecksJson)
            .HasColumnName("ConfirmationChecksJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.FinalRiskScore)
            .HasColumnName("FinalRiskScore")
            .HasColumnType("decimal(5, 2)");

        builder.Property(x => x.FinalRiskLevel)
            .HasColumnName("FinalRiskLevel")
            .HasMaxLength(20);

        builder.Property(x => x.SentToSGR)
            .HasColumnName("SentToSGR")
            .IsRequired();

        builder.Property(x => x.SentToSGRAt)
            .HasColumnName("SentToSGRAt");

        builder.Property(x => x.SentToSGRBy)
            .HasColumnName("SentToSGRBy");

        builder.Property(x => x.DynamicsEntityId)
            .HasColumnName("DynamicsEntityId")
            .HasMaxLength(100);

        builder.Property(x => x.DynamicsEntityUrl)
            .HasColumnName("DynamicsEntityUrl")
            .HasMaxLength(500);

        builder.Property(x => x.CreatedAt)
            .HasColumnName("CreatedAt")
            .IsRequired();
    }
}
