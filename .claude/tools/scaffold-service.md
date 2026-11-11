# Tool: Scaffold Service — Api.Web.ServiciosFinancieros

## Purpose
Generate a complete, production-ready service class following
all project standards. Includes interface definition, DI pattern,
tenant context usage, error handling and logging patterns.

## Usage
claude tool scaffold-service {ServiceName} {type}

Examples:
  claude tool scaffold-service DynamicsFactura dynamics
  claude tool scaffold-service TenantConfig repository
  claude tool scaffold-service FirebaseUser external

## Parameters
- ServiceName: PascalCase name without "Service" suffix
- type:
  - dynamics: integrates with Dynamics 365 via MSAL + HttpClient
  - repository: data access service using EF Core + AppDbContext
  - external: integrates with any external HTTP API
  - internal: pure business logic, no external dependencies

## Template: dynamics type

// Project : Api.Web.ServiciosFinancieros
// Created : {date}
// Modified: {date}
using Api.Web.ServiciosFinancieros.Clases;
using Api.Web.ServiciosFinancieros.Models;
using Api.Web.ServiciosFinancieros.Servicios;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Api.Web.ServiciosFinancieros.Servicios;

public interface I{ServiceName}Service
{
    Task<{ServiceName}Result?> GetAsync(
        string id,
        TenantContext tenantContext,
        CancellationToken cancellationToken);
}

public sealed class {ServiceName}Service : I{ServiceName}Service
{
    private readonly IDynamicsTokenService _dynamicsTokenService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<{ServiceName}Service> _logger;

    public {ServiceName}Service(
        IDynamicsTokenService dynamicsTokenService,
        IHttpClientFactory httpClientFactory,
        ILogger<{ServiceName}Service> logger)
    {
        _dynamicsTokenService = dynamicsTokenService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<{ServiceName}Result?> GetAsync(
        string id,
        TenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        try
        {
            var accessToken = await _dynamicsTokenService
                .AcquireTokenAsync(tenantContext, cancellationToken);

            var httpClient = _httpClientFactory.CreateClient("Dynamics");
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);
            httpClient.BaseAddress = new Uri(tenantContext.DynamicsUrl);

            var response = await httpClient.GetAsync(
                $"api/data/v9.2/{typeof({ServiceName}Result).Name}s({id})",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "{Service} GET failed for tenant {TenantId}. " +
                    "StatusCode: {StatusCode}",
                    nameof({ServiceName}Service),
                    tenantContext.TenantId,
                    response.StatusCode);

                return null;
            }

            var content = await response.Content
                .ReadAsStringAsync(cancellationToken);

            return JsonSerializer.Deserialize<{ServiceName}Result>(
                content,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex,
                "{Service} HTTP error for tenant {TenantId}",
                nameof({ServiceName}Service),
                tenantContext.TenantId);

            throw new DynamicsServiceException(
                $"Dynamics request failed for tenant {tenantContext.TenantId}",
                ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "{Service} unexpected error for tenant {TenantId}",
                nameof({ServiceName}Service),
                tenantContext.TenantId);

            throw;
        }
    }
}

## Template: repository type

// Project : Api.Web.ServiciosFinancieros
// Created : {date}
// Modified: {date}
using Api.Web.ServiciosFinancieros.Data;
using Api.Web.ServiciosFinancieros.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.ServiciosFinancieros.Data.Repositories;

public interface I{ServiceName}Repository
{
    Task<{ServiceName}?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken);

    Task<{ServiceName}?> GetByClienteAsync(
        string cliente,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<{ServiceName}>> GetAllActiveAsync(
        CancellationToken cancellationToken);
}

public sealed class {ServiceName}Repository : I{ServiceName}Repository
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<{ServiceName}Repository> _logger;

    public {ServiceName}Repository(
        AppDbContext dbContext,
        ILogger<{ServiceName}Repository> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<{ServiceName}?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken)
    {
        return await _dbContext.Set<{ServiceName}>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<{ServiceName}?> GetByClienteAsync(
        string cliente,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cliente);

        var result = await _dbContext.Set<{ServiceName}>()
            .AsNoTracking()
            .Where(x => x.Cliente == cliente)
            .Select(x => new {ServiceName}
            {
                // TODO: map only required columns
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (result is null)
        {
            _logger.LogWarning(
                "{Repository} — no record found for cliente {Cliente}",
                nameof({ServiceName}Repository),
                cliente);
        }

        return result;
    }

    public async Task<IReadOnlyCollection<{ServiceName}>> GetAllActiveAsync(
        CancellationToken cancellationToken)
    {
        return await _dbContext.Set<{ServiceName}>()
            .AsNoTracking()
            .Where(x => x.IsActive)
            .ToListAsync(cancellationToken);
    }
}

## Template: external type

// Project : Api.Web.ServiciosFinancieros
// Created : {date}
// Modified: {date}
using Api.Web.ServiciosFinancieros.Clases;
using Api.Web.ServiciosFinancieros.Models;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Api.Web.ServiciosFinancieros.Servicios;

public interface I{ServiceName}Service
{
    Task<{ServiceName}Result?> GetAsync(
        string id,
        TenantContext tenantContext,
        CancellationToken cancellationToken);
}

public sealed class {ServiceName}Service : I{ServiceName}Service
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<{ServiceName}Service> _logger;

    public {ServiceName}Service(
        IHttpClientFactory httpClientFactory,
        ILogger<{ServiceName}Service> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<{ServiceName}Result?> GetAsync(
        string id,
        TenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        try
        {
            var httpClient = _httpClientFactory.CreateClient(
                nameof({ServiceName}Service));

            var response = await httpClient.GetAsync(
                $"/{id}",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "{Service} GET failed for tenant {TenantId}. " +
                    "StatusCode: {StatusCode}",
                    nameof({ServiceName}Service),
                    tenantContext.TenantId,
                    response.StatusCode);

                return null;
            }

            var content = await response.Content
                .ReadAsStringAsync(cancellationToken);

            return JsonSerializer.Deserialize<{ServiceName}Result>(
                content,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex,
                "{Service} HTTP error for tenant {TenantId}",
                nameof({ServiceName}Service),
                tenantContext.TenantId);

            throw new ExternalServiceException(
                $"External request failed for tenant {tenantContext.TenantId}",
                ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "{Service} unexpected error for tenant {TenantId}",
                nameof({ServiceName}Service),
                tenantContext.TenantId);

            throw;
        }
    }
}

## Template: internal type

// Project : Api.Web.ServiciosFinancieros
// Created : {date}
// Modified: {date}
using Api.Web.ServiciosFinancieros.Clases;
using Api.Web.ServiciosFinancieros.Models;

namespace Api.Web.ServiciosFinancieros.Clases;

public interface I{ServiceName}Service
{
    Task<{ServiceName}Result> ProcessAsync(
        {ServiceName}Request request,
        TenantContext tenantContext,
        CancellationToken cancellationToken);
}

public sealed class {ServiceName}Service : I{ServiceName}Service
{
    private readonly ILogger<{ServiceName}Service> _logger;

    public {ServiceName}Service(
        ILogger<{ServiceName}Service> logger)
    {
        _logger = logger;
    }

    public async Task<{ServiceName}Result> ProcessAsync(
        {ServiceName}Request request,
        TenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tenantContext);

        _logger.LogInformation(
            "{Service} processing for tenant {TenantId}",
            nameof({ServiceName}Service),
            tenantContext.TenantId);

        // TODO: implement business logic

        return new {ServiceName}Result();
    }
}

## Rules Applied by This Tool
- Always sealed class
- Always interface + implementation in same file
- Always ILogger<T> injected
- Always TenantContext as explicit parameter (never injected)
- Always ArgumentNullException.ThrowIfNull at entry points
- Always ArgumentException.ThrowIfNullOrWhiteSpace for string params
- Always structured logging with TenantId and service name
- Always specific catch blocks before general Exception catch
- Always CancellationToken as last parameter in every async method
- Never inject ITenantContextAccessor in services
  — receive TenantContext as method parameter from controller
- Never return EF entities directly — always project to records or DTOs
- Never swallow exceptions silently

## Post-Generation Steps
After generating the service, remind developer to:
1. Register the service in Program.cs with correct lifetime:
   - dynamics/external: Scoped
   - repository: Scoped
   - internal: Scoped or Transient
2. Create request/response records in Models/ if not yet created
3. Inject the service interface into the corresponding controller
4. Add HttpClient registration in Program.cs for dynamics/external types