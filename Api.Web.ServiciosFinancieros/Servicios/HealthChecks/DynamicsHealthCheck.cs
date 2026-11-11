// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-29
// Modified: 2026-04-29

using Api.Web.ServiciosFinancieros.Clases;
using Api.Web.ServiciosFinancieros.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Identity.Client;

namespace Api.Web.ServiciosFinancieros.Servicios.HealthChecks;

/// <summary>
/// Verifies that the application can acquire a Dynamics 365 access token via MSAL
/// using the first active tenant credential stored in the database.
/// Never throws — returns <see cref="HealthCheckResult.Unhealthy"/> on any failure.
/// </summary>
public sealed class DynamicsHealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEncryptionService _encryptionService;
    private readonly ILogger<DynamicsHealthCheck> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="DynamicsHealthCheck"/>.
    /// </summary>
    /// <param name="scopeFactory">Factory used to create a DI scope for the scoped <see cref="AppDbContext"/>.</param>
    /// <param name="encryptionService">Service used to decrypt the stored client secret.</param>
    /// <param name="logger">Logger for structured diagnostics.</param>
    public DynamicsHealthCheck(
        IServiceScopeFactory scopeFactory,
        IEncryptionService encryptionService,
        ILogger<DynamicsHealthCheck> logger)
    {
        _scopeFactory = scopeFactory;
        _encryptionService = encryptionService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            string clientId;
            string encryptedSecret;
            string azureAdTenantId;
            string dynamicsUrl;

            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var credencial = await db.Credenciales
                    .AsNoTracking()
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (credencial is null)
                {
                    return HealthCheckResult.Unhealthy("No tenant credentials found in database");
                }

                clientId        = credencial.ClientId;
                encryptedSecret = credencial.ClientSecret;
                azureAdTenantId = credencial.AzureAdTenantId;
                dynamicsUrl     = credencial.Url;
            }

            var clientSecret = _encryptionService.Decrypt(encryptedSecret);
            var authority    = $"https://login.microsoftonline.com/{azureAdTenantId}";
            var scopes       = new[] { $"{dynamicsUrl}/.default" };

            var app = ConfidentialClientApplicationBuilder
                .Create(clientId)
                .WithClientSecret(clientSecret)
                .WithAuthority(new Uri(authority))
                .Build();

            await app.AcquireTokenForClient(scopes)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            return HealthCheckResult.Healthy("MSAL token acquired successfully");
        }
        catch (MsalException ex)
        {
            _logger.LogError(ex,
                "Dynamics health check failed. MSAL error code: {ErrorCode}", ex.ErrorCode);

            return HealthCheckResult.Degraded($"MSAL authentication failed: {ex.ErrorCode}", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dynamics health check failed with an unexpected error");
            return HealthCheckResult.Degraded("MSAL authentication failed", ex);
        }
    }
}
