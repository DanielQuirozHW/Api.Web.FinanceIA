// Project : Api.Web.FinanceIA
// Created : 2026-07-20

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="CreditLineOperation"/>.
/// Maps to [dbo].[CreditLineOperations] in the per-tenant business database.
/// Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
public sealed class CreditLineOperationConfiguration : IEntityTypeConfiguration<CreditLineOperation>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CreditLineOperation> builder)
    {
        builder.ToTable("CreditLineOperations", "dbo");

        builder.HasKey(x => x.OperationId);

        builder.Property(x => x.OperationId)
            .HasColumnName("OperationId")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.CreditLineId)
            .HasColumnName("CreditLineId")
            .IsRequired();

        builder.Property(x => x.AnalysisId)
            .HasColumnName("AnalysisId")
            .IsRequired();

        builder.Property(x => x.Type)
            .HasColumnName("Type")
            .IsRequired();

        builder.Property(x => x.Amount)
            .HasColumnName("Amount")
            .HasColumnType("decimal(18, 2)")
            .IsRequired();

        builder.Property(x => x.Currency)
            .HasColumnName("Currency")
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(x => x.ValidUntil)
            .HasColumnName("ValidUntil")
            .HasColumnType("date");

        builder.Property(x => x.Notes)
            .HasColumnName("Notes")
            .HasMaxLength(1000);

        builder.Property(x => x.Status)
            .HasColumnName("Status")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.CreatedByUserId)
            .HasColumnName("CreatedByUserId")
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("CreatedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("GETUTCDATE()")
            .IsRequired();

        // Condition flags (script 057). NOT NULL with a DB-side DEFAULT (0) that backfilled the
        // existing rows in the ALTER; the default is deliberately NOT declared here, exactly as
        // [Status]'s is not, so EF always writes the value the application decided rather than
        // letting the column default stand in for it.
        builder.Property(x => x.ASolaFirma)
            .HasColumnName("ASolaFirma")
            .IsRequired();

        builder.Property(x => x.RequiereContragarantiaReal)
            .HasColumnName("RequiereContragarantiaReal")
            .IsRequired();

        // Vigencia-confirmation audit (script 040). Both nullable with no default: an operation
        // still 'Aprobada' has nothing to record, and pre-040 rows have nothing recoverable.
        builder.Property(x => x.VigenteConfirmadaEl)
            .HasColumnName("VigenteConfirmadaEl")
            .HasColumnType("datetime2");

        builder.Property(x => x.VigenteConfirmadaPor)
            .HasColumnName("VigenteConfirmadaPor");

        // Cancellation audit (script 041), the mirror of the pair above. Both nullable with no
        // default: a live operation has nothing to record, and pre-041 rows have nothing recoverable.
        builder.Property(x => x.CanceladaEl)
            .HasColumnName("CanceladaEl")
            .HasColumnType("datetime2");

        builder.Property(x => x.CanceladaPor)
            .HasColumnName("CanceladaPor");

        builder.HasIndex(x => new { x.TenantId, x.CreditLineId })
            .HasDatabaseName("IX_CreditLineOperations_TenantId_CreditLineId");

        builder.HasIndex(x => new { x.TenantId, x.AnalysisId })
            .HasDatabaseName("IX_CreditLineOperations_TenantId_AnalysisId");
    }
}
