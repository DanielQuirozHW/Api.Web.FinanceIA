// Project : Api.Web.FinanceIA
// Created : 2026-08-31

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="Notification"/>.
/// Maps to [dbo].[Notifications] in the per-tenant business database.
/// Database First — schema changes are applied via DDL scripts, not migrations
/// (see docs/database/049_notifications_table.sql).
/// </summary>
public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications", "dbo");

        builder.HasKey(x => x.NotificationId);

        // NotificationId is a plain uniqueidentifier — the application supplies the Guid on
        // insert, mirroring Alert. Do NOT mark it ValueGeneratedOnAdd: that tells EF the DB
        // generates the key and makes EF omit the explicit Guid, which breaks the INSERT
        // silently. (The column does carry a NEWSEQUENTIALID() default in the DDL, but that
        // is there for rows written by hand, not by this application.)
        builder.Property(x => x.NotificationId)
            .HasColumnName("NotificationId")
            .ValueGeneratedNever();

        // nvarchar(100), NOT uniqueidentifier — TenantId is a Firebase slug everywhere in the
        // business database since DDL 005. Matches Alerts.TenantId exactly.
        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasColumnType("nvarchar(100)")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.UserId)
            .HasColumnName("UserId")
            .IsRequired();

        builder.Property(x => x.Type)
            .HasColumnName("Type")
            .HasColumnType("nvarchar(50)")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.Title)
            .HasColumnName("Title")
            .HasColumnType("nvarchar(300)")
            .HasMaxLength(300)
            .IsRequired();

        // Body, SourceType, SourceId and ReadAt are the only nullable columns, and each is
        // mapped WITHOUT .IsRequired() so the CLR nullability and the database agree.
        builder.Property(x => x.Body)
            .HasColumnName("Body")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.SourceType)
            .HasColumnName("SourceType")
            .HasColumnType("nvarchar(50)")
            .HasMaxLength(50);

        builder.Property(x => x.SourceId)
            .HasColumnName("SourceId");

        builder.Property(x => x.IsRead)
            .HasColumnName("IsRead")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(x => x.ReadAt)
            .HasColumnName("ReadAt")
            .HasColumnType("datetime2");

        builder.Property(x => x.CreatedAt)
            .HasColumnName("CreatedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("GETUTCDATE()")
            .IsRequired();

        // Both indexes lead with (TenantId, UserId): every read this table serves is scoped to
        // one user of one tenant. Names match 049_notifications_table.sql exactly.
        builder.HasIndex(x => new { x.TenantId, x.UserId, x.CreatedAt })
            .HasDatabaseName("IX_Notifications_TenantId_UserId_CreatedAt")
            .IsDescending(false, false, true);

        builder.HasIndex(x => new { x.TenantId, x.UserId, x.IsRead })
            .HasDatabaseName("IX_Notifications_TenantId_UserId_IsRead");

        // DDL 083: the keyset-paging index. Its key matches the listing's ORDER BY CreatedAt DESC,
        // NotificationId DESC exactly, so a page is an ordered range seek. Documentary metadata like
        // the two above — EF never creates indexes here (Database First) and --verify-schema checks
        // tables and columns only, so this mapping and the script are deploy-order independent.
        // The INCLUDE columns (IsRead, Type, SourceType, SourceId) live in the script only.
        builder.HasIndex(x => new { x.TenantId, x.UserId, x.CreatedAt, x.NotificationId })
            .HasDatabaseName("IX_Notifications_TenantId_UserId_CreatedAt_NotificationId")
            .IsDescending(false, false, true, true);
    }
}
