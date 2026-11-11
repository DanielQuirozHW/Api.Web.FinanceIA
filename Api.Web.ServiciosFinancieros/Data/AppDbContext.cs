// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-15
// Modified: 2026-04-15

using Api.Web.ServiciosFinancieros.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.ServiciosFinancieros.Data;

/// <summary>
/// EF Core DbContext for the shared Azure SQL Server database.
/// Maps tenant configuration and error log tables.
/// Database First — never run migrations against this context.
/// Lazy loading and auto detect changes are disabled for performance.
/// </summary>
public sealed class AppDbContext : DbContext
{
    /// <summary>
    /// Initializes the context with the provided options.
    /// Disables lazy loading and automatic change detection
    /// to reduce overhead on read-heavy workloads.
    /// </summary>
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
        ChangeTracker.LazyLoadingEnabled = false;
        ChangeTracker.AutoDetectChangesEnabled = false;
    }

    /// <summary>
    /// Maps to [dbo].[Credenciales].
    /// Contains Dynamics 365 OAuth credentials per tenant.
    /// </summary>
    public DbSet<TenantCredencial> Credenciales => Set<TenantCredencial>();

    /// <summary>
    /// Maps to [dbo].[TenantFirebaseConfig].
    /// Contains Firebase project configuration per tenant.
    /// </summary>
    public DbSet<TenantFirebaseConfig> TenantFirebaseConfigs => Set<TenantFirebaseConfig>();

    /// <summary>
    /// Maps to [dbo].[ErrorLogs].
    /// Persistent error log for operational review.
    /// </summary>
    public DbSet<ErrorLog> ErrorLogs => Set<ErrorLog>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
