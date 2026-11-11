// Project : Api.Web.FinanceIA
// Created : 2026-04-20
// Modified: 2026-04-20

using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Microsoft.Extensions.Logging;

namespace Api.Web.FinanceIA.Clases;

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
    /// validate input → load TenantFirebaseConfig → load Credenciales (optional) →
    /// decrypt sensitive fields → build and return TenantContext.
    /// </summary>
    /// <param name="tenantId">The tenant identifier from the X-Tenant-ID header.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>The fully resolved <see cref="TenantContext"/> for the request.</returns>
    /// <exception cref="TenantNotFoundException">
    /// Thrown when <paramref name="tenantId"/> has no TenantFirebaseConfig row.
    /// </exception>
    /// <exception cref="TenantConfigException">
    /// Thrown when Firebase config decryption fails.
    /// </exception>
    public async Task<TenantContext> ResolveAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var firebaseConfig = await _firebaseConfigRepository
            .GetByClienteAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (firebaseConfig is null)
        {
            _logger.LogWarning(
                "TenantFirebaseConfig not found for tenant {TenantId}",
                tenantId);
            throw new TenantNotFoundException(tenantId);
        }

        var credencial = await _credencialRepository
            .GetByClienteAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        string? dynamicsUrl           = null;
        string? dynamicsClientId      = null;
        string? dynamicsClientSecret  = null;
        string? azureAdTenantId       = null;

        if (credencial is null)
        {
            _logger.LogDebug(
                "Credenciales not found for tenant {TenantId}; Dynamics fields will be null",
                tenantId);
        }
        else
        {
            dynamicsUrl      = credencial.Url;
            dynamicsClientId = credencial.ClientId;
            azureAdTenantId  = credencial.AzureAdTenantId;

            try
            {
                dynamicsClientSecret = _encryptionService.Decrypt(credencial.ClientSecret);
            }
            catch (EncryptionException ex)
            {
                _logger.LogError(ex,
                    "Failed to decrypt Dynamics client secret for tenant {TenantId}",
                    tenantId);
                dynamicsClientSecret = null;
            }
        }

        string firebaseCredentialJson;
        string recaptchaSecretKey;

        try
        {
            firebaseCredentialJson = _encryptionService.Decrypt(firebaseConfig.CredentialJson);
            recaptchaSecretKey     = _encryptionService.Decrypt(firebaseConfig.RecaptchaSecretKey);
        }
        catch (EncryptionException ex)
        {
            _logger.LogError(ex,
                "Failed to decrypt Firebase config for tenant {TenantId}",
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
            DynamicsUrl            = dynamicsUrl,
            DynamicsClientId       = dynamicsClientId,
            DynamicsClientSecret   = dynamicsClientSecret,
            AzureAdTenantId        = azureAdTenantId,
            FirebaseProjectId      = firebaseConfig.ProjectId,
            FirebaseCredentialJson = firebaseCredentialJson,
            RecaptchaSecretKey     = recaptchaSecretKey,
            // Not decrypted: a Firebase Web API key is public by design and is stored
            // in plaintext. See the remarks on TenantFirebaseConfig.WebApiKey.
            FirebaseWebApiKey      = firebaseConfig.WebApiKey,
        };
    }
}
