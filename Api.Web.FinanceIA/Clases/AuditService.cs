// Project : Api.Web.FinanceIA
// Created : 2026-05-02
// Modified: 2026-05-02

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// EF Core implementation of <see cref="IAuditService"/>.
/// Writes append-only entries to AuditLogs and SessionLogs.
/// All methods catch every exception internally and log a warning
/// so that audit failures never propagate to callers.
/// Uses its own DI scope to avoid DbContext concurrency with the request pipeline.
/// </summary>
public sealed class AuditService : IAuditService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuditService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="AuditService"/>.
    /// </summary>
    public AuditService(IServiceScopeFactory scopeFactory, ILogger<AuditService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task LogAsync(
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
        CancellationToken ct = default)
    {
        // CK_AuditLogs_Severity rejects anything else, and the rejection would be swallowed below with the
        // row. Store it as a Warning and say so loudly instead: an audit row is never lost over its label.
        if (severity is null || !AuditSeverities.All.Contains(severity))
        {
            _logger.LogError(
                "Audit severity {Severity} for action {Action} tenant {TenantId} is not one CK_AuditLogs_Severity allows; written as {StoredSeverity}",
                severity, action, tenantId, AuditSeverities.Warning);
            severity = AuditSeverities.Warning;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FinanciaDbContext>();

            var entry = new AuditLog
            {
                TenantId     = tenantId,
                UserId       = userId,
                Action       = action,
                Severity     = severity,
                EntityType   = entityType,
                EntityId     = entityId,
                OldValueJson = oldValueJson,
                NewValueJson = newValueJson,
                IpAddress    = ipAddress,
                RequestId    = requestId,
                Timestamp    = DateTime.UtcNow
            };

            db.AuditLogs.Add(entry);
            db.ChangeTracker.DetectChanges();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "AuditService failed to write audit log for tenant {TenantId} action {Action}",
                tenantId,
                action);
        }
    }

    /// <inheritdoc />
    public async Task LogSessionAsync(
        Guid tenantId,
        Guid? userId,
        string email,
        string eventType,
        string? ipAddress,
        string? userAgent,
        string? failureReason = null,
        CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FinanciaDbContext>();

            var entry = new SessionLog
            {
                TenantId      = tenantId,
                UserId        = userId,
                Email         = email,
                EventType     = eventType,
                IpAddress     = ipAddress,
                UserAgent     = userAgent,
                FailureReason = failureReason,
                Timestamp     = DateTime.UtcNow
            };

            db.SessionLogs.Add(entry);
            db.ChangeTracker.DetectChanges();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "AuditService failed to write session log for tenant {TenantId} event {EventType}",
                tenantId,
                eventType);
        }
    }
}
