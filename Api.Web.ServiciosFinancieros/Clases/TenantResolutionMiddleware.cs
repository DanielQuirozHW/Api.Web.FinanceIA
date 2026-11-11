// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-20
// Modified: 2026-04-20

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Api.Web.ServiciosFinancieros.Clases.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Api.Web.ServiciosFinancieros.Clases;

/// <summary>
/// ASP.NET Core middleware that resolves the tenant for every incoming request.
/// Reads the <c>X-Tenant-ID</c> header, validates its format, loads the
/// <see cref="Models.TenantContext"/> from SQL Server via <see cref="ITenantResolver"/>
/// and stores it in both <see cref="ITenantContextAccessor"/> and
/// <see cref="HttpContext.Items"/> for downstream use.
/// Must be registered BEFORE <c>UseAuthentication()</c> in the pipeline.
/// </summary>
public sealed class TenantResolutionMiddleware
{
    private static readonly Regex TenantIdRegex =
        new(@"^[a-zA-Z0-9-]{1,100}$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly RequestDelegate _next;
    private readonly ILogger<TenantResolutionMiddleware> _logger;

    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="logger">Logger for this middleware.</param>
    public TenantResolutionMiddleware(
        RequestDelegate next,
        ILogger<TenantResolutionMiddleware> logger)
    {
        _next   = next;
        _logger = logger;
    }

    /// <summary>
    /// Validates the <c>X-Tenant-ID</c> header, resolves the tenant context and
    /// populates <see cref="ITenantContextAccessor"/> before invoking the next middleware.
    /// Returns a 400, 404 or 500 ProblemDetails response when resolution fails.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="tenantResolver">
    /// Scoped service that loads and decrypts tenant config from SQL Server.
    /// </param>
    /// <param name="tenantContextAccessor">
    /// Scoped holder that makes TenantContext available to controllers.
    /// </param>
    public async Task InvokeAsync(
        HttpContext context,
        ITenantResolver tenantResolver,
        ITenantContextAccessor tenantContextAccessor)
    {
        if (context.Request.Path.StartsWithSegments("/health") ||
            context.Request.Path.StartsWithSegments("/ready"))
        {
            await _next(context);
            return;
        }

        // Step 1 — Read X-Tenant-ID header
        var tenantId = context.Request.Headers["X-Tenant-ID"].FirstOrDefault();

        var isAuthEndpoint = context.Request.Path.StartsWithSegments("/api/bff/auth");

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            if (isAuthEndpoint)
            {
                // Auth endpoints always require the header (Firebase token has no tenantId claim)
                _logger.LogWarning("Request rejected — X-Tenant-ID header is missing");

                await WriteProblemDetailsAsync(
                    context,
                    statusCode : 400,
                    type       : "https://serviciosfinancieros.api/errors/tenant_resolution_error",
                    title      : "Bad Request",
                    detail     : "Invalid or missing tenant identifier.",
                    errorCode  : "tenant_resolution_error",
                    traceId    : Activity.Current?.Id ?? context.TraceIdentifier);

                return;
            }

            // Non-auth endpoints: no header present, defer to TenantFromJwtMiddleware
            await _next(context);
            return;
        }

        if (!TenantIdRegex.IsMatch(tenantId))
        {
            _logger.LogWarning(
                "Request rejected — X-Tenant-ID header has invalid format. TenantId: {TenantId}",
                tenantId);

            await WriteProblemDetailsAsync(
                context,
                statusCode : 400,
                type       : "https://serviciosfinancieros.api/errors/tenant_resolution_error",
                title      : "Bad Request",
                detail     : "Invalid or missing tenant identifier.",
                errorCode  : "tenant_resolution_error",
                traceId    : Activity.Current?.Id ?? context.TraceIdentifier);

            return;
        }

        // Step 2 — Resolve TenantContext
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
                detail     : "Tenant not found.",
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
                detail     : "Tenant configuration error.",
                errorCode  : "tenant_config_error",
                traceId    : Activity.Current?.Id ?? context.TraceIdentifier);

            return;
        }

        // Step 3 — Store TenantContext and continue
        tenantContextAccessor.TenantContext  = tenantContext;
        context.Items["TenantContext"]       = tenantContext;

        _logger.LogInformation(
            "TenantContext resolved. TenantId: {TenantId}",
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
