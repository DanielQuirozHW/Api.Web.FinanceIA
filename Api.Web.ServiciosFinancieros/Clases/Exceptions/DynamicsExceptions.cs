// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-21
// Modified: 2026-04-21

namespace Api.Web.ServiciosFinancieros.Clases.Exceptions;

/// <summary>
/// Thrown when MSAL fails to acquire a Dynamics 365 access token
/// (wrong credentials, expired secret, Azure AD issue). Maps to HTTP 502.
/// </summary>
public sealed class DynamicsAuthException : ExternalServiceException
{
    /// <param name="message">Internal diagnostic message (never sent to the client).</param>
    /// <param name="innerException">Original MSAL exception.</param>
    public DynamicsAuthException(string message, Exception? innerException = null)
        : base("Dynamics365", "dynamics_auth_error", 502,
               "Error connecting to financial services.", innerException)
    {
    }
}

/// <summary>
/// Thrown when a requested entity does not exist in Dynamics 365. Maps to HTTP 404.
/// </summary>
public sealed class DynamicsNotFoundException : ExternalServiceException
{
    /// <param name="message">Internal diagnostic message (never sent to the client).</param>
    /// <param name="innerException">Original exception if applicable.</param>
    public DynamicsNotFoundException(string message, Exception? innerException = null)
        : base("Dynamics365", "dynamics_not_found", 404,
               "Resource not found.", innerException)
    {
    }
}

/// <summary>
/// Thrown when Dynamics 365 returns a business rule violation
/// (e.g. account locked, insufficient balance, duplicate record).
/// Maps to HTTP 422.
/// </summary>
public sealed class DynamicsBusinessException : ExternalServiceException
{
    /// <summary>Raw Dynamics 365 response body for internal diagnostics. Never sent to client.</summary>
    public string? DynamicsResponse { get; }

    /// <param name="message">
    /// Sanitized Dynamics message safe to surface to the client,
    /// or "Business rule violation." when the original message is unsafe.
    /// </param>
    /// <param name="innerException">Original exception if applicable.</param>
    public DynamicsBusinessException(string message, Exception? innerException = null)
        : base("Dynamics365", "dynamics_business_error", 422, message, innerException)
    {
    }

    /// <param name="message">Client-safe message.</param>
    /// <param name="dynamicsResponse">Raw Dynamics 365 response body for diagnostics (never logged in full).</param>
    /// <param name="innerException">Original exception if applicable.</param>
    public DynamicsBusinessException(string message, string? dynamicsResponse, Exception? innerException = null)
        : base("Dynamics365", "dynamics_business_error", 422, message, innerException)
    {
        DynamicsResponse = dynamicsResponse;
    }
}

/// <summary>
/// Thrown when the Dynamics 365 API is unreachable or returns 5xx. Maps to HTTP 503.
/// </summary>
public sealed class DynamicsUnavailableException : ExternalServiceException
{
    /// <param name="message">Internal diagnostic message (never sent to the client).</param>
    /// <param name="innerException">Original exception if applicable.</param>
    public DynamicsUnavailableException(string message, Exception? innerException = null)
        : base("Dynamics365", "dynamics_unavailable", 503,
               "Financial services temporarily unavailable.", innerException)
    {
    }
}

/// <summary>
/// Thrown when a Dynamics 365 API call exceeds the configured timeout. Maps to HTTP 504.
/// </summary>
public sealed class DynamicsTimeoutException : ExternalServiceException
{
    /// <param name="innerException">Original timeout exception.</param>
    public DynamicsTimeoutException(Exception? innerException = null)
        : base("Dynamics365", "dynamics_timeout", 504,
               "Request to financial services timed out.", innerException)
    {
    }

    /// <param name="message">Internal diagnostic message (never sent to the client).</param>
    /// <param name="innerException">Original timeout exception.</param>
    public DynamicsTimeoutException(string message, Exception? innerException = null)
        : base("Dynamics365", "dynamics_timeout", 504,
               "Request to financial services timed out.", innerException)
    {
    }
}
