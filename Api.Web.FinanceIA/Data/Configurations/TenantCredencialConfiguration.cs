// Project : Api.Web.FinanceIA
// Created : 2026-04-15
// Modified: 2026-04-15

using Api.Web.FinanceIA.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations;

/// <summary>
/// Fluent API configuration for <see cref="TenantCredencial"/>.
/// Maps to [dbo].[Credenciales] — master tenant registry containing
/// Dynamics 365 OAuth credentials per tenant. The Conexion column
/// is obsolete and explicitly ignored. ClientSecret is stored
/// AES-256 encrypted in the database and decrypted at runtime.
/// </summary>
public sealed class TenantCredencialConfiguration
    : IEntityTypeConfiguration<TenantCredencial>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TenantCredencial> builder)
    {
        builder.ToTable("Credenciales", "dbo");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.Url)
            .HasColumnName("url")
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(x => x.ClientId)
            .HasColumnName("clientid")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.ClientSecret)
            .HasColumnName("clientsecret")
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(x => x.AzureAdTenantId)
            .HasColumnName("tenantid")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.Cliente)
            .HasColumnName("cliente")
            .HasMaxLength(100)
            .IsRequired();

        // Conexion column is obsolete — ignored
        builder.Ignore("Conexion");

        builder.HasIndex(x => x.Cliente)
            .IsUnique()
            .HasDatabaseName("UQ_Credenciales_Cliente");
    }
}
