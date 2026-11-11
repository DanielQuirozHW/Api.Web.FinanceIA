// Project : Api.Web.FinanceIA
// Created : 2026-04-20
// Modified: 2026-04-20

using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Models;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Defines the contract for creating and caching per-tenant Firebase authentication instances.
/// </summary>
public interface ITenantFirebaseRegistry
{
    /// <summary>
    /// Returns a <see cref="FirebaseAuth"/> instance bound to the given tenant's Firebase project,
    /// creating and registering the underlying <see cref="FirebaseApp"/> on first access (lazy initialization).
    /// </summary>
    /// <param name="tenantContext">The resolved tenant context containing Firebase credentials.</param>
    /// <returns>
    /// A <see cref="FirebaseAuth"/> instance bound to the tenant's <see cref="FirebaseApp"/>.
    /// Never returns the default instance.
    /// </returns>
    FirebaseAuth GetOrCreate(TenantContext tenantContext);
}

/// <summary>
/// Thread-safe singleton registry that manages one <see cref="FirebaseApp"/> instance
/// per tenant across the entire application lifetime.
/// Uses a <see cref="ConcurrentDictionary{TKey,TValue}"/> with ordinal key comparison
/// to guarantee safe concurrent access without explicit locking.
/// Implements <see cref="IDisposable"/> to delete all registered <see cref="FirebaseApp"/>
/// instances on application shutdown.
/// </summary>
/// <remarks>
/// Lifetime: Singleton — register with
/// <c>builder.Services.AddSingleton&lt;ITenantFirebaseRegistry, TenantFirebaseRegistry&gt;()</c>.
/// Never use <see cref="FirebaseApp.DefaultInstance"/> or <see cref="FirebaseAuth.DefaultInstance"/>.
/// </remarks>
public sealed class TenantFirebaseRegistry(
    ILogger<TenantFirebaseRegistry> logger) : ITenantFirebaseRegistry, IDisposable
{
    private readonly ConcurrentDictionary<string, FirebaseApp> _apps =
        new ConcurrentDictionary<string, FirebaseApp>(StringComparer.Ordinal);

    private readonly ILogger<TenantFirebaseRegistry> _logger = logger;

    /// <summary>
    /// Returns a <see cref="FirebaseAuth"/> instance for the given tenant,
    /// applying lazy initialization: the <see cref="FirebaseApp"/> is created
    /// only on the first call per tenant and cached for all subsequent calls.
    /// Concurrent requests for the same tenant are safe — the dictionary provides
    /// the required thread-safety guarantees.
    /// </summary>
    /// <param name="tenantContext">
    /// The resolved tenant context. Must not be null.
    /// <see cref="TenantContext.FirebaseCredentialJson"/> and
    /// <see cref="TenantContext.FirebaseProjectId"/> are used to initialize the app.
    /// The credential JSON content is never logged.
    /// </param>
    /// <returns>
    /// A <see cref="FirebaseAuth"/> bound to the tenant's <see cref="FirebaseApp"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="tenantContext"/> is null.
    /// </exception>
    /// <exception cref="FirebaseServiceException">
    /// Thrown when the <see cref="FirebaseApp"/> cannot be created.
    /// </exception>
    public FirebaseAuth GetOrCreate(TenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(tenantContext);

        if (_apps.TryGetValue(tenantContext.TenantId, out var existingApp))
        {
            _logger.LogDebug(
                "Returning existing FirebaseApp for tenant {TenantId}",
                tenantContext.TenantId);

            return FirebaseAuth.GetAuth(existingApp);
        }

        try
        {
            _logger.LogInformation(
                "Creating FirebaseApp for tenant {TenantId}",
                tenantContext.TenantId);

            #pragma warning disable CS0618
            var credential = GoogleCredential.FromJson(
                tenantContext.FirebaseCredentialJson);
            #pragma warning restore CS0618

            var options = new AppOptions
            {
                Credential = credential,
                ProjectId = tenantContext.FirebaseProjectId
            };

            var firebaseApp = FirebaseApp.Create(options, tenantContext.TenantId);

            _apps.TryAdd(tenantContext.TenantId, firebaseApp);

            _logger.LogInformation(
                "FirebaseApp created successfully for tenant {TenantId}",
                tenantContext.TenantId);

            return FirebaseAuth.GetAuth(firebaseApp);
        }
        catch (Exception ex) when (ex is not FirebaseServiceException)
        {
            _logger.LogError(ex,
                "Failed to create FirebaseApp for tenant {TenantId}",
                tenantContext.TenantId);

            throw new FirebaseServiceException(
                $"Failed to initialize Firebase for tenant {tenantContext.TenantId}", ex);
        }
    }

    /// <summary>
    /// Deletes all registered <see cref="FirebaseApp"/> instances using best-effort cleanup.
    /// Exceptions during individual app deletion are swallowed silently to ensure all apps
    /// are attempted. Called automatically by the DI container on application shutdown.
    /// </summary>
    public void Dispose()
    {
        foreach (var app in _apps.Values)
        {
            try
            {
                app.Delete();
            }
            catch
            {
                // Best-effort cleanup — swallow silently
            }
        }

        _apps.Clear();

        _logger.LogInformation(
            "TenantFirebaseRegistry disposed. All FirebaseApp instances deleted.");
    }
}
