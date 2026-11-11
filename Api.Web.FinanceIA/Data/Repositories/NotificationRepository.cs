// Project : Api.Web.FinanceIA
// Created : 2026-08-31

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="INotificationRepository"/>.
/// Read queries use AsNoTracking. Write operations call
/// ChangeTracker.DetectChanges() manually because AutoDetectChangesEnabled
/// is disabled on <see cref="FinanciaDbContext"/>.
/// </summary>
public sealed class NotificationRepository : INotificationRepository
{
    private readonly FinanciaDbContext _dbContext;
    private readonly ILogger<NotificationRepository> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="NotificationRepository"/>.
    /// </summary>
    public NotificationRepository(
        FinanciaDbContext dbContext,
        ILogger<NotificationRepository> logger)
    {
        _dbContext = dbContext;
        _logger    = logger;
    }

    /// <inheritdoc />
    public async Task<NotificationPage> GetPageForUserAsync(
        string tenantId, Guid userId, NotificationQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var rows = await BuildPageQuery(tenantId, userId, query)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var hasMore = rows.Count > query.Limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);

        return new NotificationPage(rows, hasMore);
    }

    /// <summary>
    /// The page query, unexecuted: scope + filters + keyset + order + TOP (Limit + 1). Internal so a
    /// test can render it for SQL Server with ToQueryString and pin that every part of it — the
    /// keyset comparison and the entityId EXISTS legs included — translates server-side.
    /// </summary>
    internal IQueryable<Notification> BuildPageQuery(string tenantId, Guid userId, NotificationQuery query)
    {
        var filtered = ApplyFilters(tenantId, userId, query);

        if (query.AfterCreatedAt is { } afterCreatedAt && query.AfterNotificationId is { } afterId)
        {
            // Strictly after the previous page's last row under (CreatedAt DESC, NotificationId DESC).
            // CompareTo(...) < 0 translates to [NotificationId] < @id on SQL Server, so the predicate and
            // the ORDER BY below use the same uniqueidentifier ordering (SQL Server's, not .NET's).
            filtered = filtered.Where(n =>
                n.CreatedAt < afterCreatedAt
                || (n.CreatedAt == afterCreatedAt && n.NotificationId.CompareTo(afterId) < 0));
        }

        // Limit + 1: the extra row only answers "is there a next page?" and is never returned.
        // IX_Notifications_TenantId_UserId_CreatedAt_NotificationId (DDL 083) matches this ORDER BY
        // key for key, so a page is an ordered range seek that stops after Limit + 1 rows.
        return filtered
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.NotificationId)
            .Take(query.Limit + 1);
    }

    /// <inheritdoc />
    public async Task<int> CountForUserAsync(
        string tenantId, Guid userId, NotificationQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        return await ApplyFilters(tenantId, userId, query)
            .CountAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> GetUnreadCountForUserAsync(
        string tenantId, Guid userId, CancellationToken ct)
    {
        return await _dbContext.Notifications
            .AsNoTracking()
            .Where(n => n.TenantId == tenantId && n.UserId == userId && !n.IsRead)
            .CountAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> MarkAsReadAsync(
        Guid notificationId, string tenantId, Guid userId, CancellationToken ct)
    {
        // UserId is part of the lookup, not just of the update. A notification addressed to another
        // user of the same tenant is "not found" here, which is what the 404 in the controller
        // reports — one user can never flip another's read state.
        var notification = await _dbContext.Notifications
            .SingleOrDefaultAsync(
                n => n.NotificationId == notificationId
                  && n.TenantId       == tenantId
                  && n.UserId         == userId,
                ct)
            .ConfigureAwait(false);

        if (notification is null) return false;

        notification.IsRead = true;
        notification.ReadAt = DateTime.UtcNow;

        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Notification {NotificationId} marked as read by {UserId} tenant {TenantId}",
            notificationId, userId, tenantId);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> MarkAsUnreadAsync(
        Guid notificationId, string tenantId, Guid userId, CancellationToken ct)
    {
        // Same three-part lookup as MarkAsReadAsync: another user's notification is "not found".
        var notification = await _dbContext.Notifications
            .SingleOrDefaultAsync(
                n => n.NotificationId == notificationId
                  && n.TenantId       == tenantId
                  && n.UserId         == userId,
                ct)
            .ConfigureAwait(false);

        if (notification is null) return false;

        // Idempotent: an already-unread row is a success with nothing to write.
        if (!notification.IsRead && notification.ReadAt is null) return true;

        notification.IsRead = false;
        notification.ReadAt = null;

        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Notification {NotificationId} marked as unread by {UserId} tenant {TenantId}",
            notificationId, userId, tenantId);

        return true;
    }

    /// <inheritdoc />
    public async Task<int> MarkManyAsReadAsync(
        string tenantId, Guid userId, IReadOnlyCollection<Guid> notificationIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notificationIds);
        if (notificationIds.Count == 0) return 0;

        var ids = notificationIds.Distinct().ToList();

        // UserId and TenantId in the predicate: an id addressed to anyone else matches nothing and is
        // not counted. Already-read rows are excluded so their ReadAt keeps the moment they were read.
        var unread = await _dbContext.Notifications
            .Where(n => n.TenantId == tenantId
                     && n.UserId   == userId
                     && !n.IsRead
                     && ids.Contains(n.NotificationId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (unread.Count == 0) return 0;

        var now = DateTime.UtcNow;
        foreach (var notification in unread)
        {
            notification.IsRead = true;
            notification.ReadAt = now;
        }

        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Marked {Count} of {RequestedCount} requested notifications as read for user {UserId} tenant {TenantId}",
            unread.Count, ids.Count, userId, tenantId);

        return unread.Count;
    }

    /// <inheritdoc />
    public async Task<int> MarkAllAsReadForUserAsync(
        string tenantId, Guid userId, CancellationToken ct)
    {
        var unread = await _dbContext.Notifications
            .Where(n => n.TenantId == tenantId && n.UserId == userId && !n.IsRead)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (unread.Count == 0) return 0;

        var now = DateTime.UtcNow;
        foreach (var notification in unread)
        {
            notification.IsRead = true;
            notification.ReadAt = now;
        }

        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Marked {Count} notifications as read for user {UserId} tenant {TenantId}",
            unread.Count, userId, tenantId);

        return unread.Count;
    }

    /// <inheritdoc />
    public async Task CreateManyAsync(IEnumerable<Notification> notifications, CancellationToken ct)
    {
        var list = notifications as ICollection<Notification> ?? notifications.ToList();
        if (list.Count == 0) return;

        _dbContext.Notifications.AddRange(list);
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Created {NotificationCount} notifications for tenant {TenantId}",
            list.Count, list.First().TenantId);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsByTypeForUserInPeriodAsync(
        string            tenantId,
        Guid              userId,
        string            type,
        DateTime          periodStartUtc,
        DateTime          periodEndUtc,
        CancellationToken ct)
    {
        // AnyAsync, not a count and not a fetch: the only question is existence, and this runs once
        // per (user, alert) on an emission path reached from a polled endpoint.
        // IX_Notifications_TenantId_UserId_CreatedAt gives the (tenant, user) seek and the Type
        // comparison is a residual filter over one user's own notifications.
        return await _dbContext.Notifications
            .AsNoTracking()
            .Where(n => n.TenantId  == tenantId
                     && n.UserId    == userId
                     && n.Type      == type
                     && n.CreatedAt >= periodStartUtc
                     && n.CreatedAt <  periodEndUtc)
            .AnyAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsByTypeForUserAndSourceAsync(
        string            tenantId,
        Guid              userId,
        string            type,
        Guid              sourceId,
        CancellationToken ct)
    {
        // AnyAsync, not a count and not a fetch: the only question is existence.
        // IX_Notifications_TenantId_UserId_CreatedAt gives the (tenant, user) seek and the Type and
        // SourceId comparisons are residual filters over one user's own notifications — a small set.
        return await _dbContext.Notifications
            .AsNoTracking()
            .Where(n => n.TenantId == tenantId
                     && n.UserId   == userId
                     && n.Type     == type
                     && n.SourceId == sourceId)
            .AnyAsync(ct)
            .ConfigureAwait(false);
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// The (TenantId, UserId) scope plus every filter of <paramref name="query"/> — shared by the page
    /// and by the count, so totalCount always describes exactly the rows the pages walk through.
    /// </summary>
    private IQueryable<Notification> ApplyFilters(string tenantId, Guid userId, NotificationQuery query)
    {
        var q = _dbContext.Notifications
            .AsNoTracking()
            .Where(n => n.TenantId == tenantId && n.UserId == userId);

        if (query.UnreadOnly)
            q = q.Where(n => !n.IsRead);

        if (query.Type is { } type)
            q = q.Where(n => n.Type == type);

        if (query.SourceType is { } sourceType)
            q = q.Where(n => n.SourceType == sourceType);

        if (query.SourceId is { } sourceId)
            q = q.Where(n => n.SourceId == sourceId);

        if (query.FromUtc is { } fromUtc)
            q = q.Where(n => n.CreatedAt >= fromUtc);

        if (query.ToUtc is { } toUtc)
            q = q.Where(n => n.CreatedAt < toUtc);

        if (query.EntityId is { } entityId)
        {
            // The PyME a notification is about is reachable from its source, and only from two of them:
            //   "Analysis"    → SourceId is AnalysisResults.ResultId → Analyses.EntityId
            //   "AnalysisRun" → SourceId is Documents.DocumentId     → Documents.EntityId
            // "Alert" rows (billing quota, tenant-wide) and rows with no source never match. Both legs are
            // correlated EXISTS semi-joins on primary keys, evaluated only over this user's own rows, so
            // they cannot multiply rows the way a JOIN would. Every leg repeats the TenantId predicate:
            // no EF global query filter exists to add it.
            const string analysisSource = NotificationCatalog.AnalysisSource;
            const string runSource      = NotificationCatalog.AnalysisRunSource;

            q = q.Where(n =>
                (n.SourceType == analysisSource
                    && _dbContext.AnalysisResults.Any(r =>
                        r.ResultId == n.SourceId
                        && r.TenantId == tenantId
                        && _dbContext.Analyses.Any(a =>
                            a.AnalysisId == r.AnalysisId
                            && a.TenantId == tenantId
                            && a.EntityId == entityId)))
                || (n.SourceType == runSource
                    && _dbContext.Documents.Any(d =>
                        d.DocumentId == n.SourceId
                        && d.TenantId == tenantId
                        && d.EntityId == entityId)));
        }

        return q;
    }
}
