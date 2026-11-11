// Project : Api.Web.FinanceIA
// Created : 2026-08-28

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="Fiador"/>.
/// Maps to [dbo].[Fiadores] in the per-tenant business database.
/// Database First — schema changes are applied via DDL scripts, not migrations
/// (docs/database/046_fiadores_table.sql).
/// </summary>
public sealed class FiadorConfiguration : IEntityTypeConfiguration<Fiador>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Fiador> builder)
    {
        builder.ToTable("Fiadores", "dbo");

        builder.HasKey(x => x.FiadorId);

        builder.Property(x => x.FiadorId)
            .HasColumnName("FiadorId")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.EntityId)
            .HasColumnName("EntityId")
            .IsRequired();

        // nvarchar(13) mirrors [dbo].[Entities].CUIT — the CUIT is a string, never a numeric type.
        builder.Property(x => x.CUIT)
            .HasColumnName("CUIT")
            .HasColumnType("nvarchar(13)")
            .HasMaxLength(13)
            .IsRequired();

        builder.Property(x => x.Denominacion)
            .HasColumnName("Denominacion")
            .HasMaxLength(300);

        builder.Property(x => x.Cargo)
            .HasColumnName("Cargo")
            .HasMaxLength(200);

        builder.Property(x => x.PorcAccionista)
            .HasColumnName("PorcAccionista")
            .HasColumnType("decimal(5, 2)");

        builder.Property(x => x.Origen)
            .HasColumnName("Origen")
            .HasMaxLength(20)
            .IsRequired();

        // Plain string, no value conversion: the JSON is deserialized to NosisData at the service
        // boundary, exactly as [dbo].[Entities].NosisDataJson is handled today.
        builder.Property(x => x.NosisDataJson)
            .HasColumnName("NosisDataJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.NosisFetchedAt)
            .HasColumnName("NosisFetchedAt")
            .HasColumnType("datetime2");

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

        // Enforces ONE Active fiador per (TenantId, EntityId, CUIT).
        builder.HasIndex(x => new { x.TenantId, x.EntityId, x.CUIT })
            .HasDatabaseName("UX_Fiadores_ActivePerEntityCuit")
            .HasFilter("[Status] = 'Active'")
            .IsUnique();

        builder.HasIndex(x => new { x.TenantId, x.EntityId })
            .HasDatabaseName("IX_Fiadores_TenantId_EntityId");

        // DDL 069. Written only by the fiador legajo link (see Fiador.PersonLegajoId); filtered
        // because it is NULL on every fiador nobody linked. NOT unique: one legajo may back the
        // fiadores of several PyMEs.
        builder.Property(x => x.PersonLegajoId)
            .HasColumnName("PersonLegajoId");

        builder.HasIndex(x => x.PersonLegajoId)
            .HasFilter("[PersonLegajoId] IS NOT NULL")
            .HasDatabaseName("IX_Fiadores_PersonLegajoId");

        // The one relationship this configuration models, and it is here for INSERT ORDER, not for
        // navigation, exactly as in EntityConfiguration. EF orders the commands of a single
        // SaveChanges by the model's foreign keys only; without this edge it falls back to ordering
        // by table name, "Fiadores" before "PersonLegajos", and the link that inserts a new legajo
        // and points a fiador at it would fail FK_Fiadores_PersonLegajos. No navigation on either
        // side. IX_Fiadores_PersonLegajoId covers the FK, so EF adds no conventional index.
        builder.HasOne<PersonLegajo>()
            .WithMany()
            .HasForeignKey(x => x.PersonLegajoId)
            .HasConstraintName("FK_Fiadores_PersonLegajos")
            .OnDelete(DeleteBehavior.Restrict);

        // DDL 074. Both NULL until the fiador's first PATCH; no default, no FK (like CreatedByUserId).
        builder.Property(x => x.UpdatedAt)
            .HasColumnName("UpdatedAt")
            .HasColumnType("datetime2");

        builder.Property(x => x.UpdatedByUserId)
            .HasColumnName("UpdatedByUserId");
    }
}
