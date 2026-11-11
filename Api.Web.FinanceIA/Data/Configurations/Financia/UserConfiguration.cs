// Project : Api.Web.FinanceIA
// Created : 2026-05-02
// Modified: 2026-05-02

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="User"/>.
/// Maps to [dbo].[Users] in the per-tenant business database.
/// Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users", "dbo");

        builder.HasKey(x => x.UserId);

        builder.Property(x => x.UserId)
            .HasColumnName("UserId")
            .ValueGeneratedNever();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasColumnType("nvarchar(100)")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.FirebaseUid)
            .HasColumnName("FirebaseUid")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(x => x.Email)
            .HasColumnName("Email")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(x => x.DisplayName)
            .HasColumnName("DisplayName")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.Role)
            .HasColumnName("Role")
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(x => x.IsActive)
            .HasColumnName("IsActive")
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(x => x.LastLoginAt)
            .HasColumnName("LastLoginAt")
            .HasColumnType("datetime2");

        builder.Property(x => x.FailedLoginCount)
            .HasColumnName("FailedLoginCount")
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(x => x.LockedUntil)
            .HasColumnName("LockedUntil")
            .HasColumnType("datetime2");

        builder.Property(x => x.MustChangePassword)
            .HasColumnName("MustChangePassword")
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(x => x.TwoFactorEnabled)
            .HasColumnName("TwoFactorEnabled")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("CreatedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("GETUTCDATE()")
            .IsRequired();

        builder.Property(x => x.CreatedBy)
            .HasColumnName("CreatedBy")
            .HasColumnType("nvarchar(100)")
            .HasMaxLength(100);

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("UpdatedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("GETUTCDATE()")
            .IsRequired();

        builder.Property(x => x.OnboardingFlagsJson)
            .HasColumnName("OnboardingFlagsJson")
            .HasColumnType("nvarchar(max)");

        // Nullable, and mapped without .IsRequired() so the CLR nullability and the database agree.
        // NULL means "not assigned to a team" — see DDL 063 for why that is a designed state and not
        // merely a permissive column.
        builder.Property(x => x.TeamId)
            .HasColumnName("TeamId");

        builder.HasIndex(x => x.FirebaseUid)
            .IsUnique()
            .HasDatabaseName("UQ_Users_FirebaseUid");

        builder.HasIndex(x => x.Email)
            .IsUnique()
            .HasDatabaseName("UQ_Users_Email");
    }
}
