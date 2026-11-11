// Project : Api.Web.FinanceIA
// Created : 2026-09-03

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="MfaBackupCode"/>.
/// Maps to [dbo].[MfaBackupCodes] in the per-tenant FinancIA business database.
/// Database First — the table is created by docs/database/054_mfa_backup_codes.sql, run once per
/// tenant business DB, never by a migration.
/// </summary>
public sealed class MfaBackupCodeConfiguration
    : IEntityTypeConfiguration<MfaBackupCode>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MfaBackupCode> builder)
    {
        builder.ToTable("MfaBackupCodes", "dbo");

        builder.HasKey(x => x.BackupCodeId);

        builder.Property(x => x.BackupCodeId)
            .HasColumnName("BackupCodeId")
            .ValueGeneratedOnAdd();

        // nvarchar(100), NOT uniqueidentifier — TenantId is a Firebase slug everywhere in the
        // business database since DDL 005. Matches Users.TenantId exactly.
        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasColumnType("nvarchar(100)")
            .HasMaxLength(100)
            .IsRequired();

        // FK_MfaBackupCodes_Users -> [dbo].[Users].[UserId], declared in the DDL only. No
        // navigation property and no HasOne here, following NotificationConfiguration: nothing
        // ever loads a code together with its user, and the constraint's job is done by the
        // database.
        builder.Property(x => x.UserId)
            .HasColumnName("UserId")
            .IsRequired();

        builder.Property(x => x.CodeHash)
            .HasColumnName("CodeHash")
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("CreatedAt")
            .HasColumnType("datetime2")
            .IsRequired();

        // Concurrency token, not decoration: it makes EF append "AND [UsedAt] IS NULL" to the
        // UPDATE that spends a code. Two concurrent requests submitting the SAME code would
        // otherwise both find it unspent, both write, and both report success — spending one
        // code twice. With the token the loser affects zero rows and gets a
        // DbUpdateConcurrencyException, which MfaBackupCodeService turns into "invalid".
        builder.Property(x => x.UsedAt)
            .HasColumnName("UsedAt")
            .HasColumnType("datetime2")
            .IsConcurrencyToken();

        builder.Property(x => x.UsedIp)
            .HasColumnName("UsedIp")
            .HasMaxLength(45);

        // Two indexes over the same column pair, so both use the NAMED HasIndex overload: the
        // unnamed one identifies an index by its property set, which would make the second call
        // reconfigure the first instead of declaring a separate index.

        // Both lead with (TenantId, UserId): every read this table serves is scoped to one user
        // of one tenant. Non-unique — a user holds a whole set of codes at once.
        builder.HasIndex(
                x => new { x.TenantId, x.UserId },
                "IX_MfaBackupCodes_TenantId_UserId")
            .HasDatabaseName("IX_MfaBackupCodes_TenantId_UserId");

        // The hot path. Validation and the remaining-count read both look only at codes that
        // have not been spent, and spent rows are kept forever as a consumption audit trail,
        // so the filtered index stays small while the table grows.
        builder.HasIndex(
                x => new { x.TenantId, x.UserId },
                "IX_MfaBackupCodes_TenantId_UserId_Unused")
            .HasDatabaseName("IX_MfaBackupCodes_TenantId_UserId_Unused")
            .HasFilter("[UsedAt] IS NULL");
    }
}
