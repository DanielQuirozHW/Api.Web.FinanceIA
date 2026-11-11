// Project : Api.Web.FinanceIA
// Created : 2026-09-04

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="UserApprovalLimit"/>.
/// Maps [dbo].[UserApprovalLimits] (DDL 059).
/// Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
public sealed class UserApprovalLimitConfiguration : IEntityTypeConfiguration<UserApprovalLimit>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UserApprovalLimit> builder)
    {
        builder.ToTable("UserApprovalLimits", "dbo");

        // Composite key (UserId, Currency): one ceiling per user per currency. TenantId is
        // deliberately NOT part of the key — UserId is already globally unique, and adding it would
        // let the same user hold two different limits. It is still carried and still filtered on,
        // because there is no global query filter on this context.
        builder.HasKey(x => new { x.UserId, x.Currency });

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.UserId)
            .HasColumnName("UserId")
            .IsRequired();

        builder.Property(x => x.Currency)
            .HasColumnName("Currency")
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(x => x.CanApprove)
            .HasColumnName("CanApprove")
            .IsRequired();

        builder.Property(x => x.MaxAmount)
            .HasColumnName("MaxAmount")
            .HasColumnType("decimal(18, 2)");

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

        builder.HasIndex(x => new { x.TenantId, x.UserId })
            .HasDatabaseName("IX_UserApprovalLimits_TenantId_UserId");
    }
}
