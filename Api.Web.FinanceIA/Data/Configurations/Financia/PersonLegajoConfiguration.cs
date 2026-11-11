// Project : Api.Web.FinanceIA
// Created : 2026-09-27

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="PersonLegajo"/>.
/// Maps to [dbo].[PersonLegajos], created by docs/database/069_person_legajos_and_entity_person_type.sql.
/// Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
public sealed class PersonLegajoConfiguration : IEntityTypeConfiguration<PersonLegajo>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PersonLegajo> builder)
    {
        builder.ToTable("PersonLegajos", "dbo");

        builder.HasKey(x => x.PersonLegajoId);

        // The application supplies the Guid, mirroring NotificationConfiguration: marking it
        // ValueGeneratedOnAdd would tell EF the database generates it. The NEWSEQUENTIALID() default
        // in the DDL is for the backfill and rows written by hand.
        builder.Property(x => x.PersonLegajoId)
            .HasColumnName("PersonLegajoId")
            .ValueGeneratedNever();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        // nvarchar(13) mirrors [dbo].[Entities].CUIT, hyphens included.
        builder.Property(x => x.CUIT)
            .HasColumnName("CUIT")
            .HasColumnType("nvarchar(13)")
            .HasMaxLength(13)
            .IsRequired();

        builder.Property(x => x.FullName)
            .HasColumnName("FullName")
            .HasMaxLength(300)
            .IsRequired();

        builder.Property(x => x.FiscalRegime)
            .HasColumnName("FiscalRegime")
            .HasMaxLength(30)
            .HasDefaultValue(FiscalRegimes.Desconocido)
            .IsRequired();

        builder.Property(x => x.MonotributoCategory)
            .HasColumnName("MonotributoCategory")
            .HasMaxLength(2);

        builder.Property(x => x.ActivityStartDate)
            .HasColumnName("ActivityStartDate")
            .HasColumnType("date");

        builder.Property(x => x.MaritalStatus)
            .HasColumnName("MaritalStatus")
            .HasMaxLength(30);

        builder.Property(x => x.PatrimonialRegime)
            .HasColumnName("PatrimonialRegime")
            .HasMaxLength(30);

        builder.Property(x => x.ArcaSnapshotJson)
            .HasColumnName("ArcaSnapshotJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.ArcaDataUpdatedAt)
            .HasColumnName("ArcaDataUpdatedAt")
            .HasColumnType("datetime2");

        builder.Property(x => x.BcraDataJson)
            .HasColumnName("BcraDataJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.BcraDataUpdatedAt)
            .HasColumnName("BcraDataUpdatedAt")
            .HasColumnType("datetime2");

        builder.Property(x => x.NosisDataJson)
            .HasColumnName("NosisDataJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.NosisDataUpdatedAt)
            .HasColumnName("NosisDataUpdatedAt")
            .HasColumnType("datetime2");

        builder.Property(x => x.CreatedBy)
            .HasColumnName("CreatedBy");

        builder.Property(x => x.CreatedAt)
            .HasColumnName("CreatedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("GETUTCDATE()")
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("UpdatedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("GETUTCDATE()")
            .IsRequired();

        // ── DDL 070: data-processing authorization ──────────────────────────────────
        // FK_PersonLegajos_ConsentDocument (→ Documents) exists in the database but is deliberately
        // NOT modelled: with FK_Documents_PersonLegajos it forms a cycle, and no write puts both rows
        // in one SaveChanges — the consent is recorded in its own write, against a document stored
        // earlier. ⚠ A future write that inserts a document and records consent against it in the
        // SAME SaveChanges must model the FK first (and break the cycle), or EF falls back to
        // table-name order — the #237 lesson recorded on EntityConfiguration.
        builder.Property(x => x.ConsentRecordedAt)
            .HasColumnName("ConsentRecordedAt")
            .HasColumnType("datetime2");

        builder.Property(x => x.ConsentDocumentId)
            .HasColumnName("ConsentDocumentId");

        builder.Property(x => x.ConsentRecordedBy)
            .HasColumnName("ConsentRecordedBy");

        // ONE legajo per person per tenant. The name matters beyond documentation:
        // PersonLegajoRepository recognises a lost insert race by this name in the provider message.
        builder.HasIndex(x => new { x.TenantId, x.CUIT })
            .IsUnique()
            .HasDatabaseName("UX_PersonLegajos_TenantId_CUIT");
    }
}
