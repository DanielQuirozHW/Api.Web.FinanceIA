// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-30
// Modified: 2026-04-30

using Api.Web.ServiciosFinancieros.Servicios.RateLimiting;

namespace Api.Web.ServiciosFinancieros.Clases.Middleware;

/// <summary>
/// ASP.NET Core middleware that enforces per-user, per-tenant and per-IP rate limits.
/// Must run AFTER <see cref="TenantResolutionMiddleware"/> and BEFORE authentication.
/// Health check, ready and Swagger endpoints are excluded.
/// </summary>
public sealed class RateLimitingMiddleware
{
    private static readonly string[] ExcludedPaths = { "/health", "/ready", "/swagger" };

    private readonly RequestDelegate   _next;
    private readonly IRateLimitService _rateLimitService;

    public RateLimitingMiddleware(RequestDelegate next, IRateLimitService rateLimitService)
    {
        _next             = next;
        _rateLimitService = rateLimitService;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (ShouldSkip(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var result = await _rateLimitService.CheckRateLimitAsync(context, context.RequestAborted);

        AddRateLimitHeaders(context.Response, result);

        if (!result.IsAllowed)
        {
            context.Response.StatusCode  = StatusCodes.Status429TooManyRequests;
            context.Response.ContentType = "application/problem+json";

            if (result.RetryAfterSeconds.HasValue)
                context.Response.Headers["Retry-After"] = result.RetryAfterSeconds.Value.ToString();

            await context.Response.WriteAsJsonAsync(new
            {
                type       = "https://serviciosfinancieros.api/errors/rate_limit_exceeded",
                title      = "Too Many Requests",
                status     = 429,
                detail     = result.ErrorMessage,
                errorCode  = result.ErrorCode,
                retryAfter = result.RetryAfterSeconds
            }, context.RequestAborted);

            return;
        }

        await _next(context);
    }

    private static bool ShouldSkip(PathString path)
    {
        foreach (var excluded in ExcludedPaths)
        {
            if (path.StartsWithSegments(excluded))
                return true;
        }

        return false;
    }

    private static void AddRateLimitHeaders(HttpResponse response, RateLimitResult result)
    {
        if (result.UserLimit > 0)
        {
            response.Headers["X-RateLimit-Limit-User"]     = result.UserLimit.ToString();
            response.Headers["X-RateLimit-Remaining-User"] = result.UserRemaining.ToString();
            response.Headers["X-RateLimit-Reset-User"]     = result.UserResetTimestamp.ToString();
        }

        if (result.TenantLimit.HasValue)
        {
            response.Headers["X-RateLimit-Limit-Tenant"]     = result.TenantLimit.Value.ToString();
            response.Headers["X-RateLimit-Remaining-Tenant"] = result.TenantRemaining.GetValueOrDefault().ToString();
            response.Headers["X-RateLimit-Reset-Tenant"]     = result.TenantResetTimestamp.GetValueOrDefault().ToString();
        }
    }
}
