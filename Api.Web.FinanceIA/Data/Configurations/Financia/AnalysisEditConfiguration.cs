// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="AnalysisEdit"/>.
/// Maps [dbo].[AnalysisEdits] (DDL 001; TenantId NVARCHAR(100) since DDL 081).
/// Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
/// <remarks>
/// Mapping it brings the table under the <c>--verify-schema</c> deploy gate. The table already exists from
/// 001 with a uniqueidentifier TenantId, so a name-only comparison would pass before 081 runs; the gate's
/// <c>SchemaExpectation.RequiredColumnTypes</c> checks this column's type for exactly that reason.
/// </remarks>
public sealed class AnalysisEditConfiguration : IEntityTypeConfiguration<AnalysisEdit>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AnalysisEdit> builder)
    {
        builder.ToTable("AnalysisEdits", "dbo");

        builder.HasKey(x => x.AnalysisEditId);

        // BIGINT IDENTITY(1,1): the database generates the key.
        builder.Property(x => x.AnalysisEditId)
            .HasColumnName("AnalysisEditId")
            .HasColumnType("bigint")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.AnalysisId)
            .HasColumnName("AnalysisId")
            .IsRequired();

        // nvarchar(100) Firebase slug since DDL 081 (001 created it uniqueidentifier; 005 skipped it).
        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.EditedBy)
            .HasColumnName("EditedBy")
            .IsRequired();

        builder.Property(x => x.FieldPath)
            .HasColumnName("FieldPath")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.OldValue)
            .HasColumnName("OldValue")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.NewValue)
            .HasColumnName("NewValue")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.EditReason)
            .HasColumnName("EditReason")
            .HasColumnType("nvarchar(max)");

        // DF_AnalysisEdits_EditedAt exists, but the application always supplies the instant.
        builder.Property(x => x.EditedAt)
            .HasColumnName("EditedAt")
            .HasColumnType("datetime2")
            .IsRequired();

        // No HasOne: this context models no relationships (as for AnalysisRescores).
        builder.HasIndex(x => new { x.TenantId, x.AnalysisId, x.EditedAt })
            .HasDatabaseName("IX_AnalysisEdits_TenantId_AnalysisId_EditedAt")
            .IsDescending(false, false, true);
    }
}
