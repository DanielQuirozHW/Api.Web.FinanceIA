// Project : Api.Web.FinanceIA
// Created : 2026-05-03
// Modified: 2026-10-25

using Api.Web.FinanceIA.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations;

/// <summary>
/// Fluent API configuration for <see cref="UserInvitation"/>.
/// Maps to [dbo].[UserInvitations] in the per-tenant business database.
/// Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
public sealed class UserInvitationConfiguration : IEntityTypeConfiguration<UserInvitation>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UserInvitation> builder)
    {
        builder.ToTable("UserInvitations", "dbo");

        builder.HasKey(x => x.InvitationId);

        builder.Property(x => x.InvitationId)
            .HasColumnName("InvitationId")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.Email)
            .HasColumnName("Email")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(x => x.Role)
            .HasColumnName("Role")
            .HasMaxLength(30)
            .IsRequired();

        // Nullable since DDL 075: NULL marks a first-admin bootstrap invitation (no inviter).
        builder.Property(x => x.InvitedByUserId)
            .HasColumnName("InvitedByUserId");

        builder.Property(x => x.Token)
            .HasColumnName("Token")
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(x => x.ExpiresAt)
            .HasColumnName("ExpiresAt")
            .HasColumnType("datetime2")
            .IsRequired();

        builder.Property(x => x.UsedAt)
            .HasColumnName("UsedAt")
            .HasColumnType("datetime2");

        builder.Property(x => x.CreatedAt)
            .HasColumnName("CreatedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("GETUTCDATE()")
            .IsRequired();

        builder.Property(x => x.RevokedAt)
            .HasColumnName("RevokedAt")
            .HasColumnType("datetime2");

        builder.Property(x => x.RevokedByUserId)
            .HasColumnName("RevokedByUserId");

        builder.HasIndex(x => x.Token)
            .IsUnique()
            .HasDatabaseName("UQ_UserInvitations_Token");

        builder.HasOne(x => x.InvitedBy)
            .WithMany()
            .HasForeignKey(x => x.InvitedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.ToTable(tb => tb.HasCheckConstraint(
            "CK_UserInvitations_Role",
            "[Role] IN ('Analyst', 'RiskManager', 'Admin', 'Auditor')"));
    }
}
