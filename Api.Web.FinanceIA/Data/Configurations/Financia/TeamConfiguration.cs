// Project : Api.Web.FinanceIA
// Created : 2026-09-10

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="Team"/>.
/// Maps [dbo].[Teams] (DDL 062).
/// Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
public sealed class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Team> builder)
    {
        builder.ToTable("Teams", "dbo");

        builder.HasKey(x => x.TeamId);

        // The application supplies the Guid on insert, mirroring Notification and Alert. Do NOT mark
        // it ValueGeneratedOnAdd: that tells EF the DB generates the key and makes EF omit the
        // explicit Guid, which breaks the INSERT silently. (The column does carry a
        // NEWSEQUENTIALID() default in the DDL, but that is there for rows written by hand.)
        builder.Property(x => x.TeamId)
            .HasColumnName("TeamId")
            .ValueGeneratedNever();

        // nvarchar(100), NOT uniqueidentifier — TenantId is a Firebase slug everywhere in the
        // business database since DDL 005. Matches Users.TenantId exactly.
        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.Name)
            .HasColumnName("Name")
            .HasMaxLength(200)
            .IsRequired();

        // Nullable, and mapped without .IsRequired() so the CLR nullability and the database agree.
        builder.Property(x => x.ManagerUserId)
            .HasColumnName("ManagerUserId");

        // NOT NULL with a database default of 1, matching DF_Teams_ApprovalQuorum in DDL 065 —
        // mirroring how UserConfiguration maps IsActive. The entity carries the same default as a
        // C# initializer, which is what actually decides the value on INSERT: EF sends every mapped
        // property, so HasDefaultValue alone would not stop an unset int reaching the column as 0.
        builder.Property(x => x.ApprovalQuorum)
            .HasColumnName("ApprovalQuorum")
            .HasDefaultValue(1)
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("CreatedAt")
            .IsRequired();

        builder.Property(x => x.CreatedBy)
            .HasColumnName("CreatedBy");

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("UpdatedAt")
            .IsRequired();

        builder.Property(x => x.UpdatedBy)
            .HasColumnName("UpdatedBy");

        // Leads with TenantId — the only read this table serves is "the teams of this tenant" — with
        // Name second so the Admin list's ORDER BY comes from the index itself. Name matches
        // 062_teams_table.sql exactly.
        builder.HasIndex(x => new { x.TenantId, x.Name })
            .HasDatabaseName("IX_Teams_TenantId_Name");
    }
}
