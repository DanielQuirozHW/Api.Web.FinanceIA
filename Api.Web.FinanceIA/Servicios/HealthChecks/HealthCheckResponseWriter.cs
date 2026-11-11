// Project : Api.Web.FinanceIA
// Created : 2026-04-29
// Modified: 2026-04-29

using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Api.Web.FinanceIA.Servicios.HealthChecks;

/// <summary>
/// Writes the <see cref="HealthReport"/> as a structured JSON response
/// with status, per-check details, and total duration.
/// Registered as the <c>ResponseWriter</c> delegate on the <c>/ready</c> endpoint.
/// </summary>
public static class HealthCheckResponseWriter
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Serializes <paramref name="report"/> to the HTTP response body as JSON.
    /// Always returns HTTP 200 regardless of health status —
    /// callers must inspect the <c>status</c> field in the body.
    /// </summary>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <param name="report">The health report produced by the health check pipeline.</param>
    public static Task WriteAsync(HttpContext httpContext, HealthReport report)
    {
        httpContext.Response.ContentType = "application/json; charset=utf-8";

        var checks = report.Entries.Select(e => new
        {
            name        = e.Key,
            status      = e.Value.Status.ToString(),
            description = e.Value.Description ?? string.Empty,
            duration    = Math.Round(e.Value.Duration.TotalMilliseconds, 1)
        });

        var body = new
        {
            status        = report.Status.ToString(),
            checks,
            totalDuration = Math.Round(report.TotalDuration.TotalMilliseconds, 1)
        };

        var json = JsonSerializer.Serialize(body, _jsonOptions);
        return httpContext.Response.WriteAsync(json);
    }
}
