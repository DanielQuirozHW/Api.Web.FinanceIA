// Project : Api.Web.FinanceIA
// Created : 2026-04-21
// Modified: 2026-04-21

using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Models;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Global exception handler registered as <see cref="IExceptionHandler"/> (ASP.NET Core 8).
/// Catches all unhandled exceptions from the middleware pipeline, logs them with full context,
/// persists them to the ErrorLogs table, and returns a sanitized RFC 7807 ProblemDetails response.
/// Never exposes stack traces, inner exception details or infrastructure information to clients.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="GlobalExceptionHandler"/>.
    /// </summary>
    /// <param name="logger">Logger for structured diagnostics.</param>
    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Handles the exception by logging it, persisting it to the error log,
    /// and writing a sanitized <see cref="ProblemDetails"/> response.
    /// </summary>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <param name="exception">The unhandled exception.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns><c>true</c> when the exception has been handled.</returns>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // Step 1 — Extract context information
        var tenantId = "unknown";
        if (httpContext.Items["TenantContext"] is TenantContext tenantContext)
        {
            tenantId = tenantContext.TenantId;
        }

        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;
        var userId = httpContext.User?.FindFirst("uid")?.Value
                     ?? httpContext.User?.FindFirst("sub")?.Value;
        var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString();
        var requestPath = httpContext.Request.Path.ToString();

        // Step 2 — Map exception to HTTP status and error code
        var (statusCode, errorCode, clientMessage, logLevel) = exception switch
        {
            TenantResolutionException ex =>
                (400, "tenant_resolution_error", ex.ClientMessage, LogLevel.Warning),
            TenantNotFoundException ex =>
                (404, "tenant_not_found", ex.ClientMessage, LogLevel.Warning),
            TenantConfigException ex =>
                (500, "tenant_config_error", ex.ClientMessage, LogLevel.Error),
            FirebaseAuthException ex =>
                (401, "firebase_auth_error", ex.ClientMessage, LogLevel.Warning),
            TokenGenerationException ex =>
                (500, "token_generation_error", ex.ClientMessage, LogLevel.Error),
            DatabaseException ex =>
                (500, "database_error", ex.ClientMessage, LogLevel.Error),
            EncryptionException ex =>
                (500, "encryption_error", ex.ClientMessage, LogLevel.Error),
            BusinessException ex =>
                (422, "business_error", ex.ClientMessage, LogLevel.Warning),
            ValidationException ex =>
                (400, "validation_error", ex.ClientMessage, LogLevel.Warning),
            DynamicsAuthException ex =>
                (502, "dynamics_auth_error", ex.ClientMessage, LogLevel.Error),
            DynamicsNotFoundException ex =>
                (404, "dynamics_not_found", ex.ClientMessage, LogLevel.Warning),
            DynamicsBusinessException ex =>
                (422, "dynamics_business_error", ex.ClientMessage, LogLevel.Warning),
            DynamicsUnavailableException ex =>
                (503, "dynamics_unavailable", ex.ClientMessage, LogLevel.Error),
            DynamicsTimeoutException ex =>
                (504, "dynamics_timeout", ex.ClientMessage, LogLevel.Error),
            FirebaseServiceException ex =>
                (502, "firebase_service_error", ex.ClientMessage, LogLevel.Error),
            RecaptchaException ex =>
                (502, "recaptcha_error", ex.ClientMessage, LogLevel.Error),
            _ =>
                (500, "internal_error", "An unexpected error occurred.", LogLevel.Critical)
        };

        // Step 3 — Log exception
        _logger.Log(
            logLevel,
            exception,
            "Unhandled {ExceptionType} for tenant {TenantId}. ErrorCode: {ErrorCode} | TraceId: {TraceId}",
            exception.GetType().Name,
            tenantId,
            errorCode,
            traceId);

        // Step 4 — Persist to ErrorLogs table (IErrorLogService is Scoped — resolve from request scope)
        var errorLogService = httpContext.RequestServices.GetService<IErrorLogService>();
        if (errorLogService is not null)
        {
            await errorLogService.LogAsync(new ErrorLogEntry(
                Level: logLevel.ToString(),
                Message: $"Unhandled {exception.GetType().Name}",
                ExceptionDetails: exception is AppException appEx
                    ? $"{appEx.GetType().Name} | ErrorCode: {appEx.ErrorCode}"
                    : exception.GetType().Name,
                Source: $"financia-bff:{tenantId}",
                Url: requestPath,
                UserId: userId,
                IpAddress: ipAddress,
                StackTrace: exception.ToString(),
                TraceId: traceId),
            cancellationToken).ConfigureAwait(false);
        }

        // Step 5 — Build ProblemDetails response
        var problemDetails = new ProblemDetails
        {
            Type = $"https://serviciosfinancieros.api/errors/{errorCode}",
            Title = GetTitleForStatusCode(statusCode),
            Status = statusCode,
            Detail = clientMessage,
            Instance = requestPath
        };

        problemDetails.Extensions["errorCode"] = errorCode;
        problemDetails.Extensions["traceId"] = traceId;

        if (exception is ValidationException validationEx)
        {
            problemDetails.Extensions["errors"] = validationEx.Errors
                .Select(e => new { field = e.Field, message = e.Message });
        }

        if (exception is ExternalServiceException serviceEx)
        {
            problemDetails.Extensions["serviceName"] = serviceEx.ServiceName;
        }

        // Step 6 — Write response
        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/problem+json";

        await httpContext.Response
            .WriteAsJsonAsync(problemDetails, cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    private static string GetTitleForStatusCode(int statusCode) =>
        statusCode switch
        {
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            422 => "Unprocessable Entity",
            500 => "Internal Server Error",
            502 => "Bad Gateway",
            503 => "Service Unavailable",
            504 => "Gateway Timeout",
            _ => "Error"
        };
}
