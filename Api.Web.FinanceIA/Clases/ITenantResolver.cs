// Project : Api.Web.FinanceIA
// Created : 2026-04-20
// Modified: 2026-04-20

using Api.Web.FinanceIA.Models;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Resolves a complete TenantContext from SQL Server
/// for the given tenant identifier. Called once per request
/// by TenantResolutionMiddleware after cache miss.
/// </summary>
public interface ITenantResolver
{
    /// <summary>
    /// Resolves TenantContext by loading Credenciales and
    /// TenantFirebaseConfig from DB, decrypting sensitive fields
    /// and building an immutable TenantContext record.
    /// Throws <see cref="Exceptions.TenantNotFoundException"/> if tenant does not exist.
    /// Throws <see cref="Exceptions.TenantConfigException"/> if config is incomplete or
    /// decryption fails.
    /// </summary>
    /// <param name="tenantId">The tenant identifier from the X-Tenant-ID header.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>The fully resolved <see cref="TenantContext"/> for the request.</returns>
    Task<TenantContext> ResolveAsync(
        string tenantId,
        CancellationToken cancellationToken);
}
