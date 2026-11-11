// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-15
// Modified: 2026-04-15

using Api.Web.ServiciosFinancieros.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.ServiciosFinancieros.Data.Configurations;

/// <summary>
/// Fluent API configuration for <see cref="TenantFirebaseConfig"/>.
/// Maps to [dbo].[TenantFirebaseConfig] — Firebase project configuration
/// per tenant. <c>CredentialJson</c> and <c>RecaptchaSecretKey</c> are
/// stored AES-256 encrypted in the database and decrypted at runtime
/// by <c>IEncryptionService</c>. Never log or expose these values.
/// </summary>
public sealed class TenantFirebaseConfigConfiguration
    : IEntityTypeConfiguration<TenantFirebaseConfig>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TenantFirebaseConfig> builder)
    {
        builder.ToTable("TenantFirebaseConfig", "dbo");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("Id")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.ProjectId)
            .HasColumnName("ProjectId")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.CredentialJson)
            .HasColumnName("CredentialJson")
            .IsRequired();

        builder.Property(x => x.RecaptchaSecretKey)
            .HasColumnName("RecaptchaSecretKey")
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(x => x.IsActive)
            .HasColumnName("IsActive")
            .IsRequired()
            .HasDefaultValue(true);

        builder.Property(x => x.CreatedAt)
            .HasColumnName("CreatedAt")
            .HasColumnType("datetime2")
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("UpdatedAt")
            .HasColumnType("datetime2")
            .IsRequired();

        builder.HasIndex(x => x.TenantId)
            .IsUnique()
            .HasDatabaseName("UQ_TenantFirebaseConfig_Cliente");

        builder.HasIndex(x => x.ProjectId)
            .IsUnique()
            .HasDatabaseName("UQ_TenantFirebaseConfig_ProjectId");
    }
}
