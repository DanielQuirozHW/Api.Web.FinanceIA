// Project : Api.Web.FinanceIA
// Created : 2026-04-20
// Modified: 2026-04-20

namespace Api.Web.FinanceIA.Clases.Exceptions;

/// <summary>
/// Thrown when the X-Tenant-ID header is missing, empty or has an invalid format.
/// Maps to HTTP 400.
/// </summary>
public sealed class TenantResolutionException : AppException
{
    public TenantResolutionException()
        : base("tenant_resolution_error", 400, TenantErrorMessages.InvalidOrMissingIdentifier)
    {
    }
}

/// <summary>
/// Thrown when the X-Tenant-ID value does not exist in the Credenciales table.
/// Maps to HTTP 404.
/// </summary>
public sealed class TenantNotFoundException : AppException
{
    /// <summary>
    /// The tenant identifier that was not found.
    /// </summary>
    public string TenantId { get; }

    /// <param name="tenantId">The tenant identifier that was not found.</param>
    public TenantNotFoundException(string tenantId)
        : base("tenant_not_found", 404, TenantErrorMessages.NotFound)
    {
        TenantId = tenantId;
    }
}

/// <summary>
/// Thrown when the tenant exists but its configuration is incomplete,
/// decryption fails or Firebase credentials are invalid.
/// Maps to HTTP 500.
/// </summary>
public sealed class TenantConfigException : AppException
{
    /// <summary>
    /// The tenant identifier whose configuration is invalid.
    /// </summary>
    public string TenantId { get; }

    /// <param name="tenantId">The tenant identifier whose configuration is invalid.</param>
    /// <param name="message">Internal diagnostic message (never sent to client).</param>
    /// <param name="innerException">Original exception if applicable.</param>
    public TenantConfigException(
        string tenantId,
        string message,
        Exception? innerException = null)
        : base("tenant_config_error", 500, TenantErrorMessages.ConfigError, innerException)
    {
        TenantId = tenantId;
    }
}
