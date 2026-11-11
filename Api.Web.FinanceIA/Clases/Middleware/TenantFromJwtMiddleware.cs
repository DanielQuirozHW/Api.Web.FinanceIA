// Project : Api.Web.FinanceIA
// Created : 2026-04-30
// Modified: 2026-04-30

using System.Diagnostics;
using System.Text.Json;
using Api.Web.FinanceIA.Clases.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Api.Web.FinanceIA.Clases.Middleware;

/// <summary>
/// ASP.NET Core middleware that resolves the tenant from the authenticated user's JWT claims.
/// Runs AFTER <c>UseAuthentication()</c> and only activates when <see cref="ITenantContextAccessor"/>
/// has not already been populated by <see cref="TenantResolutionMiddleware"/>.
/// Handles authenticated endpoints where the Internal JWT carries the <c>tenantId</c> claim
/// and no <c>X-Tenant-ID</c> header is present.
/// </summary>
public sealed class TenantFromJwtMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly RequestDelegate _next;
    private readonly ILogger<TenantFromJwtMiddleware> _logger;

    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="logger">Logger for structured diagnostics.</param>
    public TenantFromJwtMiddleware(
        RequestDelegate next,
        ILogger<TenantFromJwtMiddleware> logger)
    {
        _next   = next;
        _logger = logger;
    }

    /// <summary>
    /// Resolves <see cref="ITenantContextAccessor"/> from JWT claims when not already set.
    /// Skips health check endpoints, unauthenticated requests, and requests where
    /// tenant context was already resolved from a header.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="tenantResolver">Scoped service that loads and decrypts tenant config from SQL Server.</param>
    /// <param name="tenantContextAccessor">Scoped holder that makes TenantContext available to controllers.</param>
    public async Task InvokeAsync(
        HttpContext context,
        ITenantResolver tenantResolver,
        ITenantContextAccessor tenantContextAccessor)
    {
        // Skip if tenant was already resolved from header
        if (tenantContextAccessor.TenantContext is not null)
        {
            await _next(context);
            return;
        }

        // Skip health check endpoints
        if (context.Request.Path.StartsWithSegments("/health") ||
            context.Request.Path.StartsWithSegments("/ready"))
        {
            await _next(context);
            return;
        }

        // Skip unauthenticated requests
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            await _next(context);
            return;
        }

        var tenantId = context.User.FindFirst("tenantId")?.Value;

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            await _next(context);
            return;
        }

        Models.TenantContext tenantContext;

        try
        {
            tenantContext = await tenantResolver
                .ResolveAsync(tenantId, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (TenantNotFoundException)
        {
            _logger.LogWarning("Tenant not found. TenantId: {TenantId}", tenantId);

            await WriteProblemDetailsAsync(
                context,
                statusCode : 404,
                type       : "https://serviciosfinancieros.api/errors/tenant_not_found",
                title      : "Not Found",
                detail     : TenantErrorMessages.NotFound,
                errorCode  : "tenant_not_found",
                traceId    : Activity.Current?.Id ?? context.TraceIdentifier);

            return;
        }
        catch (TenantConfigException ex)
        {
            _logger.LogError(ex, "Tenant config error. TenantId: {TenantId}", tenantId);

            await WriteProblemDetailsAsync(
                context,
                statusCode : 500,
                type       : "https://serviciosfinancieros.api/errors/tenant_config_error",
                title      : "Internal Server Error",
                detail     : TenantErrorMessages.ConfigError,
                errorCode  : "tenant_config_error",
                traceId    : Activity.Current?.Id ?? context.TraceIdentifier);

            return;
        }

        tenantContextAccessor.TenantContext = tenantContext;
        context.Items["TenantContext"]      = tenantContext;

        _logger.LogInformation(
            "TenantContext resolved from JWT. TenantId: {TenantId}",
            tenantId);

        await _next(context);
    }

    private static async Task WriteProblemDetailsAsync(
        HttpContext context,
        int statusCode,
        string type,
        string title,
        string detail,
        string errorCode,
        string traceId)
    {
        context.Response.StatusCode  = statusCode;
        context.Response.ContentType = "application/problem+json";

        var problem = new
        {
            type,
            title,
            status = statusCode,
            detail,
            errorCode,
            traceId,
        };

        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            problem,
            JsonOptions,
            context.RequestAborted);
    }
}
