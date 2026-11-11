// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-21
// Modified: 2026-04-21

using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Api.Web.ServiciosFinancieros.Clases.Exceptions;
using Api.Web.ServiciosFinancieros.Models;

namespace Api.Web.ServiciosFinancieros.Servicios;

/// <summary>
/// Acquires Dynamics 365 access tokens using MSAL client credentials flow.
/// A new <see cref="IConfidentialClientApplication"/> is built per request using
/// the tenant-specific credentials from <see cref="TenantContext"/>.
/// MSAL handles token caching internally — the app instance does not need to be cached.
/// </summary>
public sealed class DynamicsTokenService : IDynamicsTokenService
{
    private readonly ILogger<DynamicsTokenService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="DynamicsTokenService"/>.
    /// </summary>
    /// <param name="logger">Logger for structured diagnostics.</param>
    public DynamicsTokenService(ILogger<DynamicsTokenService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> GetAccessTokenAsync(
        TenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantContext);

        var authority = $"https://login.microsoftonline.com/{tenantContext.AzureAdTenantId}";
        var scopes = new[] { $"{tenantContext.DynamicsUrl}/.default" };

        try
        {
            _logger.LogDebug(
                "Acquiring Dynamics access token for tenant {TenantId} using MSAL client credentials flow",
                tenantContext.TenantId);

            var app = ConfidentialClientApplicationBuilder
                .Create(tenantContext.DynamicsClientId)
                .WithClientSecret(tenantContext.DynamicsClientSecret)
                .WithAuthority(new Uri(authority))
                .Build();

            var result = await app
                .AcquireTokenForClient(scopes)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Dynamics access token acquired for tenant {TenantId}. Token expires at {ExpiresOn}",
                tenantContext.TenantId,
                result.ExpiresOn.ToString("yyyy-MM-dd HH:mm:ss UTC"));

            return result.AccessToken;
        }
        catch (MsalException ex)
        {
            _logger.LogError(ex,
                "MSAL authentication failed for tenant {TenantId}. Error code: {ErrorCode}",
                tenantContext.TenantId,
                ex.ErrorCode);

            throw new DynamicsAuthException(
                $"Failed to acquire Dynamics 365 access token. Error: {ex.ErrorCode}", ex);
        }
    }
}
