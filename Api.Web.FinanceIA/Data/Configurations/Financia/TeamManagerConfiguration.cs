// Project : Api.Web.FinanceIA
// Created : 2026-09-16

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="TeamManager"/>.
/// Maps [dbo].[TeamManagers] (DDL 064).
/// Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
public sealed class TeamManagerConfiguration : IEntityTypeConfiguration<TeamManager>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TeamManager> builder)
    {
        builder.ToTable("TeamManagers", "dbo");

        // COMPOSITE key, matching PK_TeamManagers in DDL 064. There is no surrogate id and no
        // ValueGeneratedNever() call to make: neither column is database-generated, so EF already
        // treats both as application-supplied.
        builder.HasKey(x => new { x.TeamId, x.UserId });

        builder.Property(x => x.TeamId)
            .HasColumnName("TeamId");

        builder.Property(x => x.UserId)
            .HasColumnName("UserId");

        // nvarchar(100), NOT uniqueidentifier — TenantId is a Firebase slug everywhere in the
        // business database since DDL 005. Matches Teams.TenantId and Users.TenantId exactly.
        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("CreatedAt")
            .IsRequired();

        // Nullable, and mapped without .IsRequired() so the CLR nullability and the database agree.
        // NULL means the row was seeded by the DBA — which is every row DDL 066 migrated.
        builder.Property(x => x.CreatedBy)
            .HasColumnName("CreatedBy");

        // NO HasIndex, deliberately. PK_TeamManagers is CLUSTERED on (TeamId, UserId), which already
        // serves the only read this table has — "every manager of this team" — as a covering seek.
        // The same argument DDL 063 makes for adding no index on Users.TeamId: an index written on
        // every membership change and read by nothing is a cost, not a win.
        //
        // No HasOne/WithMany either. The foreign keys to Teams and Users exist in the database
        // (FK_TeamManagers_Team, FK_TeamManagers_User) but this context models no relationships at
        // all — TeamConfiguration and UserConfiguration do the same.
    }
}
