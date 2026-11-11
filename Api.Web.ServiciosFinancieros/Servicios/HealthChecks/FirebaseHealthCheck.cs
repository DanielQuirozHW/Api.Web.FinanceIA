// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-29
// Modified: 2026-04-29

using Api.Web.ServiciosFinancieros.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Api.Web.ServiciosFinancieros.Servicios.HealthChecks;

/// <summary>
/// Verifies the Firebase configuration by counting the number of active tenant
/// entries in <c>[dbo].[TenantFirebaseConfig]</c>.
/// Reports <see cref="HealthCheckResult.Healthy"/> as long as the query succeeds,
/// regardless of how many tenants are configured (zero is still valid at startup).
/// Never throws — returns <see cref="HealthCheckResult.Unhealthy"/> on any failure.
/// </summary>
public sealed class FirebaseHealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FirebaseHealthCheck> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="FirebaseHealthCheck"/>.
    /// </summary>
    /// <param name="scopeFactory">Factory used to create a DI scope for the scoped <see cref="AppDbContext"/>.</param>
    /// <param name="logger">Logger for structured diagnostics.</param>
    public FirebaseHealthCheck(
        IServiceScopeFactory scopeFactory,
        ILogger<FirebaseHealthCheck> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var count = await db.TenantFirebaseConfigs
                .AsNoTracking()
                .CountAsync(c => c.IsActive, cancellationToken)
                .ConfigureAwait(false);

            return HealthCheckResult.Healthy($"{count} Firebase tenants configured");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Firebase health check failed with an unexpected error");
            return HealthCheckResult.Unhealthy("Firebase configuration check failed", ex);
        }
    }
}
