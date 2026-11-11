# Command: Scaffold DbContext — Api.Web.ServiciosFinancieros

## Purpose
Map an existing SQL Server table to EF Core following the 
Database First approach. Generates the entity model, Fluent API 
configuration and repository. Never generates migrations.

## Usage
/scaffold-dbcontext {TableName}
/scaffold-dbcontext Credenciales
/scaffold-dbcontext TenantFirebaseConfig

## Rules
- Always Database First — never modify the real table schema
- Always use Fluent API — never Data Annotations on entity classes
- Always map column names explicitly to match exact DB column names
- Always ignore obsolete or unused columns explicitly
- Always use AsNoTracking() in all read queries
- Always project to DTOs — never return EF entities from repositories
- Never add navigation properties unless explicitly requested
- Never use EF Core conventions for column naming
- Never generate migrations

## Files Generated
For /scaffold-dbcontext {TableName}, generate in order:

1. Models/{TableName}.cs          ← EF entity class
2. Data/Configurations/{TableName}Configuration.cs  ← Fluent API config
3. Data/Repositories/I{TableName}Repository.cs      ← interface
4. Data/Repositories/{TableName}Repository.cs       ← implementation
5. Remind developer to add DbSet to AppDbContext

## Template: Models/{TableName}.cs

// Project : Api.Web.ServiciosFinancieros
// Created : {date}
// Modified: {date}
namespace Api.Web.ServiciosFinancieros.Models;

/// <summary>
/// Maps to [dbo].[{TableName}] table in Azure SQL Server.
/// Managed externally — do not modify schema via EF Core.
/// </summary>
public sealed class {TableName}
{
    public int Id { get; set; }
    // TODO: add properties matching exact DB columns
    // Use PascalCase here — Fluent API maps to real column names
}

## Template: Data/Configurations/{TableName}Configuration.cs

// Project : Api.Web.ServiciosFinancieros
// Created : {date}
// Modified: {date}
using Api.Web.ServiciosFinancieros.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.ServiciosFinancieros.Data.Configurations;

public sealed class {TableName}Configuration 
    : IEntityTypeConfiguration<{TableName}>
{
    public void Configure(EntityTypeBuilder<{TableName}> builder)
    {
        builder.ToTable("{TableName}", "dbo");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        // TODO: map each property to its exact column name
        // Example:
        // builder.Property(x => x.Cliente)
        //     .HasColumnName("cliente")
        //     .HasMaxLength(100)
        //     .IsRequired();

        // TODO: ignore obsolete columns
        // Example:
        // builder.Ignore(x => x.Conexion);
    }
}

## Template: Data/Repositories/I{TableName}Repository.cs

// Project : Api.Web.ServiciosFinancieros
// Created : {date}
// Modified: {date}
namespace Api.Web.ServiciosFinancieros.Data.Repositories;

public interface I{TableName}Repository
{
    Task<{TableName}?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken);

    Task<{TableName}?> GetByClienteAsync(
        string cliente,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<{TableName}>> GetAllActiveAsync(
        CancellationToken cancellationToken);
}

## Template: Data/Repositories/{TableName}Repository.cs

// Project : Api.Web.ServiciosFinancieros
// Created : {date}
// Modified: {date}
using Api.Web.ServiciosFinancieros.Data;
using Api.Web.ServiciosFinancieros.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.ServiciosFinancieros.Data.Repositories;

public sealed class {TableName}Repository : I{TableName}Repository
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<{TableName}Repository> _logger;

    public {TableName}Repository(
        AppDbContext dbContext,
        ILogger<{TableName}Repository> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<{TableName}?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken)
    {
        return await _dbContext.Set<{TableName}>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.Id == id,
                cancellationToken);
    }

    public async Task<{TableName}?> GetByClienteAsync(
        string cliente,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cliente);

        var result = await _dbContext.Set<{TableName}>()
            .AsNoTracking()
            .Where(x => x.Cliente == cliente)
            .Select(x => new {TableName}
            {
                // TODO: map only required columns
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (result is null)
        {
            _logger.LogWarning(
                "{Repository} — no record found for " +
                "cliente {Cliente}",
                nameof({TableName}Repository),
                cliente);
        }

        return result;
    }

    public async Task<IReadOnlyCollection<{TableName}>> GetAllActiveAsync(
        CancellationToken cancellationToken)
    {
        return await _dbContext.Set<{TableName}>()
            .AsNoTracking()
            .Where(x => x.IsActive)
            .ToListAsync(cancellationToken);
    }
}

## Predefined: /scaffold-dbcontext Credenciales
When this specific command is called, generate with 
exact mapping of the existing Credenciales table:

### Models/TenantCredencial.cs
// Project : Api.Web.ServiciosFinancieros
// Created : {date}
// Modified: {date}
namespace Api.Web.ServiciosFinancieros.Models;

/// <summary>
/// Maps to [dbo].[Credenciales] table in Azure SQL Server.
/// Master tenant registry. Contains Dynamics 365 OAuth 
/// credentials per tenant. Managed externally — do not 
/// modify schema via EF Core.
/// </summary>
public sealed class TenantCredencial
{
    public int Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string AzureAdTenantId { get; set; } = string.Empty;
    public string Cliente { get; set; } = string.Empty;

    // Conexion column is obsolete — ignored in EF mapping
}

### Data/Configurations/TenantCredencialConfiguration.cs
// Project : Api.Web.ServiciosFinancieros
// Created : {date}
// Modified: {date}
using Api.Web.ServiciosFinancieros.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.ServiciosFinancieros.Data.Configurations;

public sealed class TenantCredencialConfiguration
    : IEntityTypeConfiguration<TenantCredencial>
{
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

        // Obsolete column — ignored
        builder.Ignore("Conexion");

        builder.HasIndex(x => x.Cliente)
            .IsUnique()
            .HasDatabaseName("UQ_Credenciales_Cliente");
    }
}

## Predefined: /scaffold-dbcontext TenantFirebaseConfig
When this specific command is called, generate with
exact mapping of the TenantFirebaseConfig table:

### Models/TenantFirebaseConfig.cs
// Project : Api.Web.ServiciosFinancieros
// Created : {date}
// Modified: {date}
namespace Api.Web.ServiciosFinancieros.Models;

/// <summary>
/// Maps to [dbo].[TenantFirebaseConfig] table in Azure SQL Server.
/// Contains Firebase project configuration per tenant.
/// CredentialJson and RecaptchaSecretKey are AES-256 encrypted.
/// Managed externally — do not modify schema via EF Core.
/// </summary>
public sealed class TenantFirebaseConfig
{
    public int Id { get; set; }
    public string Cliente { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string CredentialJson { get; set; } = string.Empty;
    public string RecaptchaSecretKey { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

### Data/Configurations/TenantFirebaseConfigConfiguration.cs
// Project : Api.Web.ServiciosFinancieros
// Created : {date}
// Modified: {date}
using Api.Web.ServiciosFinancieros.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.ServiciosFinancieros.Data.Configurations;

public sealed class TenantFirebaseConfigConfiguration
    : IEntityTypeConfiguration<TenantFirebaseConfig>
{
    public void Configure(EntityTypeBuilder<TenantFirebaseConfig> builder)
    {
        builder.ToTable("TenantFirebaseConfig", "dbo");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("Id")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.Cliente)
            .HasColumnName("Cliente")
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
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("CreatedAt")
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("UpdatedAt")
            .IsRequired();

        builder.HasIndex(x => x.Cliente)
            .IsUnique()
            .HasDatabaseName("UQ_TenantFirebaseConfig_Cliente");

        builder.HasIndex(x => x.ProjectId)
            .IsUnique()
            .HasDatabaseName("UQ_TenantFirebaseConfig_ProjectId");
    }
}

## Post-Generation Steps
After generating all files, remind developer to:

1. Add DbSet properties to AppDbContext:
   public DbSet<TenantCredencial> Credenciales → dbset name
   public DbSet<TenantFirebaseConfig> TenantFirebaseConfigs → dbset name

2. Register configuration in AppDbContext.OnModelCreating:
   modelBuilder.ApplyConfiguration(new TenantCredencialConfiguration());
   modelBuilder.ApplyConfiguration(new TenantFirebaseConfigConfiguration());

3. Register repository in Program.cs:
   builder.Services.AddScoped
       I{TableName}Repository, 
       {TableName}Repository>();

4. Verify column mappings match exact DB schema
   before running the application