// Project : Api.Web.FinanceIA
// Created : 2026-05-02
// Modified: 2026-05-02

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Writes audit and session log entries to the per-tenant business database.
/// Implementations must be fire-and-forget safe: they must catch all exceptions
/// internally and never propagate them to callers. Audit failure must never
/// interrupt the main request flow.
/// </summary>
public interface IAuditService
{
    /// <summary>
    /// Appends an audit log entry for a user action or system event.
    /// </summary>
    /// <param name="tenantId">Tenant that owns the entry.</param>
    /// <param name="userId">User who performed the action, or null for system events.</param>
    /// <param name="action">Action identifier, e.g. <c>User.Register</c>.</param>
    /// <param name="severity">
    /// Severity level, one of <see cref="AuditSeverities"/> (the CK_AuditLogs_Severity set). Default: <c>Info</c>.
    /// </param>
    /// <param name="entityType">Type of entity affected, e.g. <c>User</c>.</param>
    /// <param name="entityId">String identifier of the affected entity.</param>
    /// <param name="ipAddress">Client IP address.</param>
    /// <param name="requestId">ASP.NET Core request trace ID for correlation.</param>
    /// <param name="oldValueJson">Optional JSON snapshot of the entity state before the action.</param>
    /// <param name="newValueJson">Optional JSON snapshot of the entity state after the action.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task LogAsync(
        string tenantId,
        Guid? userId,
        string action,
        string severity = AuditSeverities.Info,
        string? entityType = null,
        string? entityId = null,
        string? ipAddress = null,
        string? requestId = null,
        string? oldValueJson = null,
        string? newValueJson = null,
        CancellationToken ct = default);

    /// <summary>
    /// Appends a session event log entry for authentication lifecycle events.
    /// </summary>
    /// <param name="tenantId">Tenant that owns the entry.</param>
    /// <param name="userId">User associated with the session, or null for pre-auth failures.</param>
    /// <param name="email">Email address used during the attempt.</param>
    /// <param name="eventType">Event type, e.g. <c>Login.Success</c>, <c>Login.Failed</c>.</param>
    /// <param name="ipAddress">Client IP address.</param>
    /// <param name="userAgent">User-Agent header from the request.</param>
    /// <param name="failureReason">Reason for failure when the event is unsuccessful.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task LogSessionAsync(
        Guid tenantId,
        Guid? userId,
        string email,
        string eventType,
        string? ipAddress,
        string? userAgent,
        string? failureReason = null,
        CancellationToken ct = default);
}
