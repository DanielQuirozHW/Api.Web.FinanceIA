// Project : Api.Web.FinanceIA
// Created : 2026-05-02
// Modified: 2026-05-02

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="SessionLog"/>.
/// Maps to [dbo].[SessionLogs] — append-only session event log.
/// Records are never updated or deleted from application code.
/// </summary>
public sealed class SessionLogConfiguration : IEntityTypeConfiguration<SessionLog>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SessionLog> builder)
    {
        builder.ToTable("SessionLogs", "dbo");

        builder.HasKey(x => x.SessionLogId);

        builder.Property(x => x.SessionLogId)
            .HasColumnName("SessionLogId")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .IsRequired();

        builder.Property(x => x.UserId)
            .HasColumnName("UserId");

        builder.Property(x => x.Email)
            .HasColumnName("Email");

        builder.Property(x => x.EventType)
            .HasColumnName("EventType")
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(x => x.IpAddress)
            .HasColumnName("IpAddress");

        builder.Property(x => x.UserAgent)
            .HasColumnName("UserAgent");

        builder.Property(x => x.FailureReason)
            .HasColumnName("FailureReason");

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
