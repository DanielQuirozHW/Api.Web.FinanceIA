// Project : Api.Web.FinanceIA
// Created : 2026-04-15
// Modified: 2026-04-15

using Api.Web.FinanceIA.Data.Configurations;
using Api.Web.FinanceIA.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data;

/// <summary>
/// EF Core DbContext for the shared "Default" Azure SQL Server database
/// (ConnectionStrings:Default). Maps tenant configuration and error log tables ONLY —
/// all business data, MFA backup codes included, belongs to FinanciaDbContext.
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
        // Apply ONLY this database's configurations, one by one, mirroring FinanciaDbContext.
        // ApplyConfigurationsFromAssembly was used here before and could not stay: it applies
        // EVERY IEntityTypeConfiguration in the assembly, which silently mapped the whole
        // business schema — Users, Entities, MfaBackupCodes and the rest — into the shared
        // "Default" database's model. Nothing queried them, so the mistake was invisible; it is
        // also exactly the confusion that put MfaBackupCodes in the wrong database to begin with.
        modelBuilder.ApplyConfiguration(new TenantCredencialConfiguration());
        modelBuilder.ApplyConfiguration(new TenantFirebaseConfigConfiguration());
        modelBuilder.ApplyConfiguration(new ErrorLogConfiguration());
    }
}
