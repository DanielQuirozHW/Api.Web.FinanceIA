// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-21
// Modified: 2026-04-21

using Api.Web.ServiciosFinancieros.Models;

namespace Api.Web.ServiciosFinancieros.Clases;

/// <summary>
/// Verifies a reCAPTCHA V3 token against Google's API
/// using the tenant-specific secret key. Never validates against
/// a global or default key.
/// </summary>
public interface IRecaptchaVerifier
{
    /// <summary>
    /// Verifies a reCAPTCHA V3 token against Google's siteverify endpoint
    /// using the secret key from the resolved <paramref name="tenantContext"/>.
    /// </summary>
    /// <param name="token">The reCAPTCHA token from the client. Must not be null or whitespace.</param>
    /// <param name="tenantContext">The resolved tenant context providing the secret key. Must not be null.</param>
    /// <param name="remoteIp">Optional client IP address to include in the verification request.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>The <see cref="RecaptchaResponse"/> from Google's siteverify API.</returns>
    /// <exception cref="Api.Web.ServiciosFinancieros.Clases.Exceptions.RecaptchaException">
    /// Thrown when the HTTP request fails, times out, or the response cannot be parsed.
    /// </exception>
    Task<RecaptchaResponse> VerifyAsync(
        string token,
        TenantContext tenantContext,
        string? remoteIp,
        CancellationToken cancellationToken);
}
