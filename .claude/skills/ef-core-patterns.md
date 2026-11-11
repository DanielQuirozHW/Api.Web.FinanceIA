# EF Core Patterns — Api.Web.ServiciosFinancieros

## Core Principles
- Database First approach exclusively — never Code First migrations
- EF Core is used strictly for data access, never for schema management
- Never run dotnet ef migrations add or dotnet ef database update
- Schema changes are handled via DDL scripts in /docs/database/
- DbContext maps existing tables — never creates or modifies them

## DbContext Configuration
- One DbContext per database: AppDbContext for the shared/config database
- DbContext registered as Scoped in DI
- Always disable lazy loading explicitly:
  options.UseLazyLoadingProxies(false)
- Always disable automatic tracking for query operations:
  Use AsNoTracking() on every read-only query
- Connection string sourced from TenantContext for tenant databases
- Never hardcode connection strings in DbContext or repositories

## Entity Configuration
- Always use Fluent API for entity configuration over Data Annotations
- Entity configuration classes implement IEntityTypeConfiguration<T>
- One configuration class per entity, placed in Data/Configurations/
- Always map table name explicitly:
  builder.ToTable("Credenciales")
- Always map column names explicitly to match existing DB schema:
  builder.Property(x => x.ClientId).HasColumnName("clientid")
- Never rely on EF Core naming conventions to match existing columns
- Exclude obsolete or unused columns with:
  builder.Ignore(x => x.Conexion)
- Always define max lengths for string columns matching DB schema:
  builder.Property(x => x.ClientId).HasMaxLength(200).IsRequired()

## Existing Table Mapping Rules
- Map Credenciales table with exact column names from existing schema:
  - id → Id (int, identity, PK)
  - conexion → ignored (obsolete field)
  - url → Url
  - clientid → ClientId
  - clientsecret → ClientSecret (encrypted in DB)
  - tenantid → AzureAdTenantId
  - cliente → Cliente (tenant identifier, unique)
- Never rename or alter existing columns via EF configuration
- If a column is unused, ignore it explicitly rather than mapping it

## Repository Pattern
- Every entity has a corresponding repository interface and implementation
- Repository interfaces defined in Data/ folder
- Repository implementations defined in Data/ folder
- Repositories are registered as Scoped in DI
- Never inject DbContext directly into controllers or services
- Always inject repository interfaces, never concrete implementations
- Repository method naming conventions:
  - GetByIdAsync(int id, CancellationToken ct)
  - GetByClienteAsync(string cliente, CancellationToken ct)
  - GetAllActiveAsync(CancellationToken ct)
  - AddAsync(TEntity entity, CancellationToken ct)
  - UpdateAsync(TEntity entity, CancellationToken ct)

## Query Patterns
- Always use async methods: ToListAsync, FirstOrDefaultAsync, SingleOrDefaultAsync
- Always pass CancellationToken to every EF Core async method
- Always use AsNoTracking() for read-only queries
- Always project to DTOs or records using Select() 
  — never return EF entities directly from repositories
- Use FirstOrDefaultAsync for single-record lookups — handle null explicitly
- Never use SingleOrDefaultAsync in hot paths (throws on duplicates)
- Prefer explicit column selection over SELECT * patterns:
  context.Credenciales
    .AsNoTracking()
    .Where(x => x.Cliente == tenantId)
    .Select(x => new TenantCredencialDto
    {
        ClientId = x.ClientId,
        ClientSecret = x.ClientSecret,
        Url = x.Url,
        AzureAdTenantId = x.AzureAdTenantId,
        Cliente = x.Cliente
    })
    .FirstOrDefaultAsync(ct)

## Write Operations
- Always use explicit transactions for multi-step write operations
- Always call SaveChangesAsync with CancellationToken
- Never use SaveChanges (synchronous)
- For update operations, fetch the entity first, modify, then save
- Never use ExecuteUpdateAsync or ExecuteDeleteAsync 
  without explicit confirmation — these bypass change tracking

## Connection String Management
- AppDbContext (config/shared DB): connection string from appsettings.json
- Tenant DbContext (per-tenant DB): connection string built from 
  TenantContext at runtime — never stored in appsettings
- Never log connection strings
- Always use encrypted connection strings in production

## Error Handling in Repositories
- Catch DbUpdateException for write operation failures
- Log the error with tenantId and operation name (never log query parameters)
- Rethrow as domain-specific exception or let it propagate to controller
- Never return null silently — use explicit null handling with meaningful logs:
  if (credencial is null)
  {
      _logger.LogWarning("Credenciales not found for tenant {TenantId}", tenantId);
      return null;
  }

## Configurations Folder Structure
Data/
├── AppDbContext.cs
├── Configurations/
│   ├── CredencialConfiguration.cs
│   └── TenantFirebaseConfigConfiguration.cs
└── Repositories/
    ├── ICredencialRepository.cs
    ├── CredencialRepository.cs
    ├── ITenantFirebaseConfigRepository.cs
    └── TenantFirebaseConfigRepository.cs