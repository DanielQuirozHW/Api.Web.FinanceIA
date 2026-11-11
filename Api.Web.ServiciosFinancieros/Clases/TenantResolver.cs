// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-20
// Modified: 2026-04-20

using Api.Web.ServiciosFinancieros.Clases.Exceptions;
using Api.Web.ServiciosFinancieros.Data.Repositories;
using Api.Web.ServiciosFinancieros.Models;
using Microsoft.Extensions.Logging;

namespace Api.Web.ServiciosFinancieros.Clases;

/// <summary>
/// Resolves a complete <see cref="TenantContext"/> from SQL Server by loading
/// <see cref="TenantCredencial"/> and <see cref="TenantFirebaseConfig"/>,
/// decrypting sensitive fields with <see cref="IEncryptionService"/> and
/// returning an immutable record. Called by TenantResolutionMiddleware on cache miss.
/// Never caches decrypted secrets.
/// </summary>
public sealed class TenantResolver(
    ICredencialRepository credencialRepository,
    ITenantFirebaseConfigRepository firebaseConfigRepository,
    IEncryptionService encryptionService,
    ILogger<TenantResolver> logger) : ITenantResolver
{
    private readonly ICredencialRepository _credencialRepository = credencialRepository;
    private readonly ITenantFirebaseConfigRepository _firebaseConfigRepository = firebaseConfigRepository;
    private readonly IEncryptionService _encryptionService = encryptionService;
    private readonly ILogger<TenantResolver> _logger = logger;

    /// <summary>
    /// Resolves the <see cref="TenantContext"/> for the given
    /// <paramref name="tenantId"/> following these steps:
    /// validate input → load Credenciales → load TenantFirebaseConfig →
    /// decrypt sensitive fields → build and return TenantContext.
    /// </summary>
    /// <param name="tenantId">The tenant identifier from the X-Tenant-ID header.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>The fully resolved <see cref="TenantContext"/> for the request.</returns>
    /// <exception cref="TenantNotFoundException">
    /// Thrown when <paramref name="tenantId"/> is not found in Credenciales.
    /// </exception>
    /// <exception cref="TenantConfigException">
    /// Thrown when Firebase config is missing/inactive or decryption fails.
    /// </exception>
    public async Task<TenantContext> ResolveAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var credencial = await _credencialRepository
            .GetByClienteAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (credencial is null)
        {
            _logger.LogWarning("Tenant {TenantId} not found in Credenciales", tenantId);
            throw new TenantNotFoundException(tenantId);
        }

        var firebaseConfig = await _firebaseConfigRepository
            .GetByClienteAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (firebaseConfig is null)
        {
            _logger.LogWarning(
                "TenantFirebaseConfig not found for tenant {TenantId}",
                tenantId);
            throw new TenantConfigException(tenantId, "Firebase configuration not found or inactive.");
        }

        string dynamicsClientSecret;
        string firebaseCredentialJson;
        string recaptchaSecretKey;

        try
        {
            dynamicsClientSecret   = _encryptionService.Decrypt(credencial.ClientSecret);
            firebaseCredentialJson = _encryptionService.Decrypt(firebaseConfig.CredentialJson);
            recaptchaSecretKey     = _encryptionService.Decrypt(firebaseConfig.RecaptchaSecretKey);
        }
        catch (EncryptionException ex)
        {
            _logger.LogError(ex,
                "Failed to decrypt config for tenant {TenantId}",
                tenantId);
            throw new TenantConfigException(
                tenantId,
                "Failed to decrypt tenant configuration.",
                ex);
        }

        _logger.LogInformation(
            "TenantContext resolved for tenant {TenantId}",
            tenantId);

        return new TenantContext
        {
            TenantId               = tenantId,
            DynamicsUrl            = credencial.Url,
            DynamicsClientId       = credencial.ClientId,
            DynamicsClientSecret   = dynamicsClientSecret,
            AzureAdTenantId        = credencial.AzureAdTenantId,
            FirebaseProjectId      = firebaseConfig.ProjectId,
            FirebaseCredentialJson = firebaseCredentialJson,
            RecaptchaSecretKey     = recaptchaSecretKey,
        };
    }
}
