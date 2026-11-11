// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-29
// Modified: 2026-04-29

using System.Diagnostics;
using System.Text;
using Api.Web.ServiciosFinancieros.Clases;
using Api.Web.ServiciosFinancieros.Data;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Api.Web.ServiciosFinancieros.Servicios.HealthChecks;

/// <summary>
/// Verifies Firestore connectivity by performing a lightweight document read
/// against the first active tenant's Firebase project.
/// Returns <see cref="HealthCheckResult.Degraded"/> when the round-trip exceeds
/// <see cref="DegradedThresholdMs"/> milliseconds, and
/// <see cref="HealthCheckResult.Unhealthy"/> on any exception.
/// Never throws — swallows all exceptions and maps them to an unhealthy result.
/// </summary>
public sealed class FirestoreHealthCheck : IHealthCheck
{
    /// <summary>
    /// Round-trip threshold above which the check is considered degraded (3 seconds).
    /// </summary>
    private const double DegradedThresholdMs = 3_000.0;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEncryptionService _encryptionService;
    private readonly ILogger<FirestoreHealthCheck> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="FirestoreHealthCheck"/>.
    /// </summary>
    /// <param name="scopeFactory">Factory used to create a DI scope for the scoped <see cref="AppDbContext"/>.</param>
    /// <param name="encryptionService">Service used to decrypt the stored Firebase credential JSON.</param>
    /// <param name="logger">Logger for structured diagnostics.</param>
    public FirestoreHealthCheck(
        IServiceScopeFactory scopeFactory,
        IEncryptionService encryptionService,
        ILogger<FirestoreHealthCheck> logger)
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
            string projectId;
            string encryptedCredentialJson;

            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var config = await db.TenantFirebaseConfigs
                    .AsNoTracking()
                    .Where(c => c.IsActive)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (config is null)
                {
                    return HealthCheckResult.Unhealthy(
                        "No active Firebase tenant configuration found");
                }

                projectId              = config.ProjectId;
                encryptedCredentialJson = config.CredentialJson;
            }

            var credentialJson = _encryptionService.Decrypt(encryptedCredentialJson);

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(credentialJson));
            var credential = GoogleCredential.FromStream(stream);

            var firestoreDb = new FirestoreDbBuilder
            {
                ProjectId  = projectId,
                Credential = credential
            }.Build();

            var sw = Stopwatch.StartNew();

            await firestoreDb.Document("_health/ping")
                .GetSnapshotAsync(cancellationToken)
                .ConfigureAwait(false);

            sw.Stop();

            if (sw.Elapsed.TotalMilliseconds >= DegradedThresholdMs)
            {
                _logger.LogWarning(
                    "Firestore health check degraded. Round-trip: {ElapsedMs:F1} ms",
                    sw.Elapsed.TotalMilliseconds);

                return HealthCheckResult.Degraded("Connection slower than expected");
            }

            return HealthCheckResult.Healthy("Firestore connection successful");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Firestore health check failed with an unexpected error");
            return HealthCheckResult.Unhealthy("Firestore connection failed", ex);
        }
    }
}
