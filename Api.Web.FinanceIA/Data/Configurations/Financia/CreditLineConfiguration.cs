// Project : Api.Web.FinanceIA
// Created : 2026-07-20

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="CreditLine"/>.
/// Maps to [dbo].[CreditLines] in the per-tenant business database.
/// Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
public sealed class CreditLineConfiguration : IEntityTypeConfiguration<CreditLine>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CreditLine> builder)
    {
        builder.ToTable("CreditLines", "dbo");

        builder.HasKey(x => x.CreditLineId);

        builder.Property(x => x.CreditLineId)
            .HasColumnName("CreditLineId")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.EntityId)
            .HasColumnName("EntityId")
            .IsRequired();

        builder.Property(x => x.Amount)
            .HasColumnName("Amount")
            .HasColumnType("decimal(18, 2)")
            .IsRequired();

        builder.Property(x => x.Currency)
            .HasColumnName("Currency")
            .HasMaxLength(3)
            .IsRequired();

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

        builder.Property(x => x.LastRaisedAt)
            .HasColumnName("LastRaisedAt")
            .HasColumnType("datetime2");

        builder.Property(x => x.LastRaiseAnalysisId)
            .HasColumnName("LastRaiseAnalysisId");

        builder.Property(x => x.OriginAnalysisId)
            .HasColumnName("OriginAnalysisId");

        // Enforces ONE Active credit line per (TenantId, EntityId, Currency).
        builder.HasIndex(x => new { x.TenantId, x.EntityId, x.Currency })
            .HasDatabaseName("UX_CreditLines_ActivePerEntityCurrency")
            .HasFilter("[Status] = 'Active'")
            .IsUnique();

        builder.HasIndex(x => new { x.TenantId, x.EntityId })
            .HasDatabaseName("IX_CreditLines_TenantId_EntityId");
    }
}
