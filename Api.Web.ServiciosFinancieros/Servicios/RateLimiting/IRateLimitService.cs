// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-30
// Modified: 2026-04-30

using Microsoft.AspNetCore.Http;

namespace Api.Web.ServiciosFinancieros.Servicios.RateLimiting;

/// <summary>
/// Evaluates rate limit policies for an incoming HTTP request.
/// Checks per-user, per-tenant and per-IP limits depending on the endpoint.
/// </summary>
public interface IRateLimitService
{
    /// <summary>
    /// Checks whether the request is within configured rate limits.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="RateLimitResult"/> that indicates whether the request is allowed
    /// and carries the data needed for rate-limit response headers.
    /// </returns>
    Task<RateLimitResult> CheckRateLimitAsync(
        HttpContext context,
        CancellationToken cancellationToken = default);
}
