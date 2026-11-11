// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-05-04

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="Entity"/>.
/// Maps to [dbo].[Entities] in the per-tenant business database.
/// Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
public sealed class EntityConfiguration : IEntityTypeConfiguration<Entity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Entity> builder)
    {
        builder.ToTable("Entities", "dbo");

        builder.HasKey(x => x.EntityId);

        builder.Property(x => x.EntityId)
            .HasColumnName("EntityId")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.CUIT)
            .HasColumnName("CUIT")
            .HasColumnType("nvarchar(13)")
            .HasMaxLength(13)
            .IsRequired();

        // DDL 069. HasDefaultValue documents DF_Entities_PersonType; with a string the sentinel is
        // null, and the property is initialised to Juridica, so EF always sends the value — unlike
        // IsMonitored, where a false equals the bool sentinel and is left to the column default.
        builder.Property(x => x.PersonType)
            .HasColumnName("PersonType")
            .HasMaxLength(10)
            .HasDefaultValue(PersonTypes.Juridica)
            .IsRequired();

        builder.Property(x => x.FiscalRegime)
            .HasColumnName("FiscalRegime")
            .HasMaxLength(30);

        builder.Property(x => x.PersonLegajoId)
            .HasColumnName("PersonLegajoId");

        builder.Property(x => x.BusinessName)
            .HasColumnName("BusinessName")
            .HasMaxLength(300)
            .IsRequired();

        builder.Property(x => x.TradeName)
            .HasColumnName("TradeName")
            .HasMaxLength(300);

        builder.Property(x => x.Sector)
            .HasColumnName("Sector")
            .HasMaxLength(100);

        builder.Property(x => x.MipymeCategory)
            .HasColumnName("MipymeCategory")
            .HasMaxLength(10);

        builder.Property(x => x.SgrOneClickEntityId)
            .HasColumnName("SgrOneClickEntityId")
            .HasMaxLength(100);

        builder.Property(x => x.RiskScore)
            .HasColumnName("RiskScore")
            .HasColumnType("decimal(5,2)");

        builder.Property(x => x.RiskScoreUpdatedAt)
            .HasColumnName("RiskScoreUpdatedAt")
            .HasColumnType("datetime2");

        builder.Property(x => x.IsMonitored)
            .HasColumnName("IsMonitored")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("CreatedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("GETUTCDATE()")
            .IsRequired();

        builder.Property(x => x.CreatedBy)
            .HasColumnName("CreatedBy");

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("UpdatedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("GETUTCDATE()")
            .IsRequired();

        builder.Property(x => x.FiscalDataJson)
            .HasColumnName("FiscalDataJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.FiscalDataUpdatedAt)
            .HasColumnName("FiscalDataUpdatedAt")
            .HasColumnType("datetime2");

        builder.Property(x => x.LufeDataJson)
            .HasColumnName("LufeDataJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.LufeDataUpdatedAt)
            .HasColumnName("LufeDataUpdatedAt")
            .HasColumnType("datetime2");

        builder.Property(x => x.BcraDataUpdatedAt)
            .HasColumnName("BcraDataUpdatedAt")
            .HasColumnType("datetime2");

        builder.Property(x => x.CasfogDataJson)
            .HasColumnName("CasfogDataJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.CasfogDataUpdatedAt)
            .HasColumnName("CasfogDataUpdatedAt")
            .HasColumnType("datetime2");

        builder.Property(x => x.NosisDataJson)
            .HasColumnName("NosisDataJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.NosisDataUpdatedAt)
            .HasColumnName("NosisDataUpdatedAt")
            .HasColumnType("datetime2");

        // DDL 087 — the NOSIS Manager report. Read only through NosisReportRepository's projection;
        // the entity list, detail and name reads never select it.
        builder.Property(x => x.NosisReportJson)
            .HasColumnName("NosisReportJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.NosisReportDate)
            .HasColumnName("NosisReportDate")
            .HasColumnType("datetime2");

        builder.Property(x => x.NosisReportImportedAt)
            .HasColumnName("NosisReportImportedAt")
            .HasColumnType("datetime2");

        builder.HasIndex(x => x.TenantId)
            .HasDatabaseName("IX_Entities_TenantId");

        // CUIT is unique PER TENANT since DDL 069, which swapped the global UQ_Entities_CUIT for this.
        // The name matters beyond documentation: the repositories recognise a lost create race by it.
        builder.HasIndex(x => new { x.TenantId, x.CUIT })
            .IsUnique()
            .HasDatabaseName("UQ_Entities_TenantId_CUIT");

        // At most one entity per legajo (DDL 069). Filtered because every Juridica has a NULL here.
        builder.HasIndex(x => x.PersonLegajoId)
            .IsUnique()
            .HasFilter("[PersonLegajoId] IS NOT NULL")
            .HasDatabaseName("UX_Entities_PersonLegajoId");

        // The one relationship this configuration models, and it is here for INSERT ORDER, not for
        // navigation. EF orders the commands of a single SaveChanges by the model's foreign keys only;
        // without this edge it falls back to ordering by table name, "Entities" before
        // "PersonLegajos", and the create that inserts both would fail FK_Entities_PersonLegajos.
        // No navigation property on either side (see Entity.PersonLegajoId), WithMany rather than
        // WithOne because UX_Entities_PersonLegajoId already enforces the 1:1 in the database — the
        // same shape as UserInvitationConfiguration's InvitedBy.
        builder.HasOne<PersonLegajo>()
            .WithMany()
            .HasForeignKey(x => x.PersonLegajoId)
            .HasConstraintName("FK_Entities_PersonLegajos")
            .OnDelete(DeleteBehavior.Restrict);

        // Serves the list page: WHERE TenantId = @t ORDER BY CreatedAt DESC, EntityId
        // OFFSET/FETCH. Declared here for the same reason IX_Entities_TenantId above is — this is
        // Database First, migrations are never run, so the mapping is documentation that keeps the
        // model honest about what the DBA actually created. Created by
        // docs/database/045_entities_add_tenant_createdat_index.sql; until that script runs the
        // query is correct but sorts instead of seeking. Note this index makes IX_Entities_TenantId
        // a redundant prefix — dropping it is a separate, DBA-owned decision.
        builder.HasIndex(x => new { x.TenantId, x.CreatedAt, x.EntityId })
            .IsDescending(false, true, false)
            .HasDatabaseName("IX_Entities_TenantId_CreatedAt");
    }
}
