// Project : Api.Web.FinanceIA
// Created : 2025-01-01
// Modified: 2025-01-01

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// Maps to [dbo].[ErrorLogs] table in Azure SQL Server.
/// Persistent error log for operational review by the team.
/// Used in combination with Application Insights telemetry.
/// Managed externally — never modify schema via EF Core.
/// </summary>
public sealed class ErrorLog
{
    /// <summary>
    /// Primary key. Maps to [ErrorId] column.
    /// </summary>
    public int ErrorId { get; set; }

    /// <summary>
    /// Error timestamp in UTC.
    /// Maps to [Timestamp] column.
    /// Always set to DateTime.UtcNow at creation time.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Error severity level.
    /// Maps to [Level] column.
    /// Valid values: Information, Warning, Error, Critical.
    /// </summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>
    /// Human readable error description.
    /// Maps to [Message] column.
    /// Always a safe, operation-level description.
    /// Never contains raw exception messages or sensitive data.
    /// Example: "Firebase token validation failed"
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Exception type and error code for diagnostic purposes.
    /// Maps to [ExceptionDetails] column.
    /// Format: "{ExceptionType} | ErrorCode: {errorCode}"
    /// Example: "FirebaseAuthException | ErrorCode: firebase_auth_error"
    /// Never contains credential values or token strings.
    /// </summary>
    public string? ExceptionDetails { get; set; }

    /// <summary>
    /// Origin of the error, in the product-wide "{origin}:{tenant}" convention.
    /// Maps to [Source] column.
    /// Origin prefixes: "financia-bff" for errors raised by this API,
    /// "financia-portal" for browser errors reported by the Portal through
    /// POST /api/errorlog/client. The tenant half is TenantContext.TenantId
    /// when available, and "unknown" when tenant resolution did not happen.
    /// Example: "financia-bff:SgrOneClickSandboxFB2", "financia-portal:unknown".
    /// Rows written before the convention was introduced hold a bare tenant slug
    /// with no prefix — queries filtering on this column must tolerate both shapes.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// API request path that originated the error.
    /// Maps to [Url] column.
    /// Example: /api/bff/obo-token
    /// Never contains query string values with sensitive data.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// End user identifier from JWT claims.
    /// Maps to [UserId] column.
    /// Value from end_user_uid or sub claim when available.
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Client IP address.
    /// Maps to [IPAddress] column.
    /// Value from HttpContext.Connection.RemoteIpAddress.
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>
    /// Full exception stack trace for diagnostic purposes.
    /// Maps to [StackTrace] column.
    /// Value from ex.ToString() — never just ex.Message.
    /// Never contains credential values or token strings.
    /// </summary>
    public string? StackTrace { get; set; }

    /// <summary>
    /// Correlation ID linking this error to Application Insights
    /// telemetry and the full request trace.
    /// Maps to [TraceId] column.
    /// Value from Activity.Current?.Id ?? HttpContext.TraceIdentifier.
    /// Use this to correlate errors across SQL Server and
    /// Application Insights for the same request.
    /// </summary>
    public string? TraceId { get; set; }
}

/// <summary>
/// Immutable entry used to create a new ErrorLog record.
/// Passed to IErrorLogService.LogAsync by GlobalExceptionHandler
/// and other error handling components.
/// </summary>
public sealed record ErrorLogEntry(
    string Level,
    string Message,
    string? ExceptionDetails = null,
    string? Source = null,
    string? Url = null,
    string? UserId = null,
    string? IpAddress = null,
    string? StackTrace = null,
    string? TraceId = null);