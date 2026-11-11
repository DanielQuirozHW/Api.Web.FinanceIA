// Project : Api.Web.FinanceIA
// Created : 2026-05-02
// Modified: 2026-05-02

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="AuditLog"/>.
/// Maps to [dbo].[AuditLogs] — append-only audit trail.
/// Records are never updated or deleted from application code.
/// </summary>
public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs", "dbo");

        builder.HasKey(x => x.AuditLogId);

        builder.Property(x => x.AuditLogId)
            .HasColumnName("AuditLogId")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.UserId)
            .HasColumnName("UserId");

        builder.Property(x => x.Action)
            .HasColumnName("Action")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.EntityType)
            .HasColumnName("EntityType");

        builder.Property(x => x.EntityId)
            .HasColumnName("EntityId");

        builder.Property(x => x.OldValueJson)
            .HasColumnName("OldValueJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.NewValueJson)
            .HasColumnName("NewValueJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.IpAddress)
            .HasColumnName("IpAddress");

        builder.Property(x => x.RequestId)
            .HasColumnName("RequestId");

        builder.Property(x => x.Source)
            .HasColumnName("Source");

        builder.Property(x => x.Severity)
            .HasColumnName("Severity")
            .HasMaxLength(20)
            .HasDefaultValue("Info")
            .IsRequired();

        builder.Property(x => x.Timestamp)
            .HasColumnName("Timestamp")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("GETUTCDATE()")
            .IsRequired();

        // Append-only: EF Core must never generate UPDATE or DELETE for this entity.
        builder.ToTable(tb =>
        {
            tb.ExcludeFromMigrations();
        });
    }
}
