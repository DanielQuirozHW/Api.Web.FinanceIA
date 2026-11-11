// Project : Api.Web.FinanceIA
// Created : 2026-06-06

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IAlertRepository"/>.
/// Read queries use AsNoTracking. Write operations call
/// ChangeTracker.DetectChanges() manually because AutoDetectChangesEnabled
/// is disabled on <see cref="FinanciaDbContext"/>.
/// </summary>
public sealed class AlertRepository : IAlertRepository
{
    private readonly FinanciaDbContext _dbContext;
    private readonly ILogger<AlertRepository> _logger;

    public AlertRepository(FinanciaDbContext dbContext, ILogger<AlertRepository> logger)
    {
        _dbContext = dbContext;
        _logger    = logger;
    }

    /// <inheritdoc />
    public async Task CreateManyAsync(IEnumerable<Alert> alerts, CancellationToken ct)
    {
        var list = alerts as ICollection<Alert> ?? alerts.ToList();
        if (list.Count == 0) return;

        _dbContext.Alerts.AddRange(list);
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Created {AlertCount} alerts for tenant {TenantId}",
            list.Count, list.First().TenantId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Alert>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
    {
        return await _dbContext.Alerts
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Alert>> GetByEntityIdAsync(
        Guid entityId, string tenantId, CancellationToken ct)
    {
        return await _dbContext.Alerts
            .AsNoTracking()
            .Where(a => a.EntityId == entityId && a.TenantId == tenantId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Alert?> GetByIdAsync(Guid alertId, string tenantId, CancellationToken ct)
    {
        return await _dbContext.Alerts
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.AlertId == alertId && a.TenantId == tenantId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> MarkAsReadAsync(Guid alertId, string tenantId, Guid userId, CancellationToken ct)
    {
        var alert = await _dbContext.Alerts
            .SingleOrDefaultAsync(a => a.AlertId == alertId && a.TenantId == tenantId, ct)
            .ConfigureAwait(false);

        if (alert is null) return false;

        alert.IsRead = true;
        alert.ReadBy = userId;
        alert.ReadAt = DateTime.UtcNow;

        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Alert {AlertId} marked as read by {UserId} tenant {TenantId}",
            alertId, userId, tenantId);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> MarkAsResolvedAsync(Guid alertId, string tenantId, Guid userId, CancellationToken ct)
    {
        var alert = await _dbContext.Alerts
            .SingleOrDefaultAsync(a => a.AlertId == alertId && a.TenantId == tenantId, ct)
            .ConfigureAwait(false);

        if (alert is null) return false;

        alert.IsResolved = true;
        alert.ResolvedBy = userId;
        alert.ResolvedAt = DateTime.UtcNow;

        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Alert {AlertId} marked as resolved by {UserId} tenant {TenantId}",
            alertId, userId, tenantId);

        return true;
    }

    /// <inheritdoc />
    public async Task<int> SupersedePreviousEntityAlertsAsync(
        Guid entityId, string tenantId, Guid? excludeAnalysisId, CancellationToken ct)
    {
        // Load tracked (no AsNoTracking) so we can mutate and persist. Scope is ALWAYS
        // tenant + entity, and only still-active alerts are touched so we never overwrite
        // a manual resolution. excludeAnalysisId protects the current analysis's own alerts.
        // The event alerts (cupo and PyME) are excluded: a new analysis does not cancel an expired
        // operation or improve a BCRA situación, and CupoAlertService resolves them when the situation
        // itself clears — including PYME_REVIEW_DUE once the new analysis is approved.
        var query = _dbContext.Alerts
            .Where(a => a.TenantId == tenantId
                     && a.EntityId == entityId
                     && !a.IsResolved
                     && !EventAlertTypes.All.Contains(a.AlertType));

        if (excludeAnalysisId.HasValue)
            query = query.Where(a => a.AnalysisId != excludeAnalysisId.Value);

        var toSupersede = await query.ToListAsync(ct).ConfigureAwait(false);

        if (toSupersede.Count == 0) return 0;

        var now = DateTime.UtcNow;
        foreach (var alert in toSupersede)
        {
            alert.IsResolved = true;
            alert.ResolvedAt = now;
            alert.ResolvedBy = null; // system auto-resolution (not a user action)
        }

        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Superseded {Count} previous active alerts for entity {EntityId} tenant {TenantId}",
            toSupersede.Count, entityId, tenantId);

        return toSupersede.Count;
    }

    /// <inheritdoc />
    public async Task<int> MarkAllAsReadAsync(string tenantId, Guid userId, CancellationToken ct)
    {
        var unread = await _dbContext.Alerts
            .Where(a => a.TenantId == tenantId && !a.IsRead)
            .ToListAsync(ct).ConfigureAwait(false);

        if (unread.Count == 0) return 0;

        var now = DateTime.UtcNow;
        foreach (var alert in unread)
        {
            alert.IsRead = true;
            alert.ReadBy = userId;
            alert.ReadAt = now;
        }

        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Marked {Count} alerts as read for tenant {TenantId}",
            unread.Count, tenantId);

        return unread.Count;
    }

    /// <inheritdoc />
    public async Task<bool> ExistsByTypeInPeriodAsync(
        string tenantId,
        string alertType,
        DateTime periodStartUtc,
        DateTime periodEndUtc,
        CancellationToken ct)
    {
        // AnyAsync, not a count and not a fetch: this runs on a polled endpoint and the only
        // question is existence. No index covers (TenantId, AlertType, CreatedAt) — IX_Alerts_TenantId
        // gives the tenant seek and the rest is a residual filter over one tenant's alerts, which is
        // far cheaper than GetAllByTenantAsync, the unpaged read the alerts list already does.
        return await _dbContext.Alerts
            .AsNoTracking()
            .Where(a => a.TenantId  == tenantId
                     && a.AlertType == alertType
                     && a.CreatedAt >= periodStartUtc
                     && a.CreatedAt <  periodEndUtc)
            .AnyAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Alert>> GetOpenByTypesForUpdateAsync(
        string tenantId, IReadOnlyCollection<string> alertTypes, CancellationToken ct)
    {
        // Tracked on purpose (no AsNoTracking): the caller mutates these and SaveReconciliationAsync
        // persists them, the fetch-modify-save shape the other writes here use.
        return await _dbContext.Alerts
            .Where(a => a.TenantId == tenantId
                     && !a.IsResolved
                     && alertTypes.Contains(a.AlertType))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Alert>> GetOpenByTypesCreatedBeforeForUpdateAsync(
        string tenantId, IReadOnlyCollection<string> alertTypes, DateTime createdBeforeUtc, CancellationToken ct)
    {
        // Tracked, like GetOpenByTypesForUpdateAsync: the caller resolves these in place and they ride
        // the same SaveReconciliationAsync. Strict '<' keeps the period half-open.
        return await _dbContext.Alerts
            .Where(a => a.TenantId == tenantId
                     && !a.IsResolved
                     && alertTypes.Contains(a.AlertType)
                     && a.CreatedAt < createdBeforeUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<(string AlertType, string MetricKey), Alert>> GetLatestByMetricKeysAsync(
        string tenantId,
        IReadOnlyCollection<string> alertTypes,
        IReadOnlyCollection<string> metricKeys,
        CancellationToken ct)
    {
        if (alertTypes.Count == 0 || metricKeys.Count == 0)
            return new Dictionary<(string, string), Alert>();

        // The history of a handful of keys, and the latest per pair picked here rather than with a
        // GroupBy-First the in-memory provider and SQL Server translate differently. A pair only has
        // one row per episode, so this stays small.
        var rows = await _dbContext.Alerts
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId
                     && alertTypes.Contains(a.AlertType)
                     && metricKeys.Contains(a.MetricKey!))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .GroupBy(a => (a.AlertType, MetricKey: a.MetricKey ?? string.Empty))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.CreatedAt).First());
    }

    /// <inheritdoc />
    public async Task SaveReconciliationAsync(
        string tenantId, IReadOnlyCollection<Alert> toCreate, CancellationToken ct)
    {
        if (toCreate.Any(a => !string.Equals(a.TenantId, tenantId, StringComparison.Ordinal)))
            throw new ArgumentException("Every alert must belong to the tenant being reconciled.", nameof(toCreate));

        if (toCreate.Count > 0)
            _dbContext.Alerts.AddRange(toCreate);

        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PreviousAlertRow>> GetPreviousByKeysAsync(
        string tenantId, IReadOnlyCollection<string> metricKeys, CancellationToken ct)
    {
        if (metricKeys.Count == 0) return Array.Empty<PreviousAlertRow>();

        return await PreviousByKeysQuery(tenantId, metricKeys)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AlertUnresolveOutcome> UnresolveAsync(Guid alertId, string tenantId, CancellationToken ct)
    {
        var alert = await _dbContext.Alerts
            .SingleOrDefaultAsync(a => a.AlertId == alertId && a.TenantId == tenantId, ct)
            .ConfigureAwait(false);

        if (alert is null) return AlertUnresolveOutcome.NotFound;
        if (!alert.IsResolved) return AlertUnresolveOutcome.Reopened;

        // The system resolves when the situation cleared (or a re-analysis superseded it): there is nothing
        // a user dismissed to take back.
        if (alert.ResolvedBy is null) return AlertUnresolveOutcome.AutoResolved;

        // One OPEN alert per situation. For an event alert the key already names the cupo or PyME; EntityId
        // keeps two PyMEs' analysis findings, which can share a MetricKey, from blocking each other.
        var hasOpenTwin = await _dbContext.Alerts
            .AsNoTracking()
            .AnyAsync(a => a.TenantId  == tenantId
                        && a.AlertId   != alertId
                        && !a.IsResolved
                        && a.AlertType == alert.AlertType
                        && a.MetricKey == alert.MetricKey
                        && a.EntityId  == alert.EntityId, ct)
            .ConfigureAwait(false);

        if (hasOpenTwin) return AlertUnresolveOutcome.OpenDuplicate;

        alert.IsResolved = false;
        alert.ResolvedAt = null;
        alert.ResolvedBy = null;

        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Alert {AlertId} reopened tenant {TenantId}",
            alertId, tenantId);

        return AlertUnresolveOutcome.Reopened;
    }

    // Test seam (InternalsVisibleTo): the SQL of the reopenedAfter read, which the in-memory provider
    // cannot prove.
    internal IQueryable<PreviousAlertRow> PreviousByKeysQuery(string tenantId, IReadOnlyCollection<string> metricKeys)
        => _dbContext.Alerts
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId
                     && a.IsResolved
                     && EventAlertTypes.All.Contains(a.AlertType)
                     && metricKeys.Contains(a.MetricKey!))
            .Select(a => new PreviousAlertRow(
                a.AlertId, a.AlertType, a.MetricKey!, a.CreatedAt, a.ResolvedAt, a.ResolvedBy, a.MetricValue));
}
