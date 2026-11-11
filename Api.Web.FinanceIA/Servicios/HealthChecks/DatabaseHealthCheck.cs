// Project : Api.Web.FinanceIA
// Created : 2026-04-29
// Modified: 2026-04-29

using Api.Web.FinanceIA.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Api.Web.FinanceIA.Servicios.HealthChecks;

/// <summary>
/// Verifies that the application can open a connection to Azure SQL Server
/// by delegating to <see cref="Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade.CanConnectAsync"/>.
/// Never throws — returns <see cref="HealthCheckResult.Unhealthy"/> on any failure.
/// </summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatabaseHealthCheck> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="DatabaseHealthCheck"/>.
    /// </summary>
    /// <param name="scopeFactory">Factory used to create a DI scope for the scoped <see cref="AppDbContext"/>.</param>
    /// <param name="logger">Logger for structured diagnostics.</param>
    public DatabaseHealthCheck(
        IServiceScopeFactory scopeFactory,
        ILogger<DatabaseHealthCheck> logger)
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

            var canConnect = await db.Database
                .CanConnectAsync(cancellationToken)
                .ConfigureAwait(false);

            return canConnect
                ? HealthCheckResult.Healthy("SQL Server connection successful")
                : HealthCheckResult.Unhealthy("SQL Server connection failed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database health check failed with an unexpected error");
            return HealthCheckResult.Unhealthy("SQL Server connection failed", ex);
        }
    }
}
