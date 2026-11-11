// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-25

using Api.Web.FinanceIA.Clases;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>An <see cref="IAuditService"/> that keeps every entry it is given, for the suites that assert on the trail.</summary>
internal sealed class RecordingAuditService : IAuditService
{
    public List<(string TenantId, Guid? UserId, string Action, string? EntityType, string? EntityId, string? OldValueJson, string? NewValueJson)> Entries { get; } = [];

    public Task LogAsync(
        string tenantId, Guid? userId, string action, string severity = "Info",
        string? entityType = null, string? entityId = null, string? ipAddress = null,
        string? requestId = null, string? oldValueJson = null, string? newValueJson = null,
        CancellationToken ct = default)
    {
        Entries.Add((tenantId, userId, action, entityType, entityId, oldValueJson, newValueJson));
        return Task.CompletedTask;
    }

    public Task LogSessionAsync(
        Guid tenantId, Guid? userId, string email, string eventType,
        string? ipAddress, string? userAgent, string? failureReason = null,
        CancellationToken ct = default)
        => Task.CompletedTask;
}
