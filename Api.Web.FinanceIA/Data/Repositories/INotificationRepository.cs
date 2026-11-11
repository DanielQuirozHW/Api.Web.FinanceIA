// Project : Api.Web.FinanceIA
// Created : 2026-08-31

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Persistence operations for <see cref="Notification"/> records
/// in the per-tenant business database.
/// </summary>
/// <remarks>
/// Every method is scoped by BOTH tenant and user. The tenant predicate is the standard rule for
/// <see cref="FinanciaDbContext"/> (there is no EF global query filter). The USER predicate is a
/// second, equally load-bearing guard: it is the only thing stopping one user of a tenant from
/// reading — or marking read — another user's notifications.
/// </remarks>
public interface INotificationRepository
{
    /// <summary>
    /// Returns one keyset page of the notifications addressed to one user, ordered by
    /// CreatedAt DESC, NotificationId DESC, under the filters of <paramref name="query"/>.
    /// </summary>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="userId">The addressee (always enforced).</param>
    /// <param name="query">Validated filters, page size and (optionally) the position to continue after.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <remarks>
    /// Keyset, never OFFSET: the page after (c, id) is every row with CreatedAt &lt; c, or CreatedAt = c
    /// and NotificationId &lt; id. NotificationId is the tie-breaker, so rows sharing a CreatedAt (a
    /// fan-out writes one <c>now</c> for all its rows) are neither skipped nor repeated at a page edge,
    /// and a notification arriving between two polls cannot shift the next page.
    /// </remarks>
    Task<NotificationPage> GetPageForUserAsync(
        string tenantId, Guid userId, NotificationQuery query, CancellationToken ct);

    /// <summary>
    /// Counts the user's notifications matching the filters of <paramref name="query"/>, ignoring its
    /// page position and size — the listing's <c>totalCount</c>.
    /// </summary>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="userId">The addressee (always enforced).</param>
    /// <param name="query">The same filters the page was read with.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<int> CountForUserAsync(
        string tenantId, Guid userId, NotificationQuery query, CancellationToken ct);

    /// <summary>
    /// Returns how many of the user's notifications are still unread.
    /// </summary>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="userId">The addressee (always enforced).</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<int> GetUnreadCountForUserAsync(string tenantId, Guid userId, CancellationToken ct);

    /// <summary>
    /// Marks one notification as read, setting IsRead and ReadAt.
    /// </summary>
    /// <param name="notificationId">The notification to mark.</param>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="userId">
    /// The addressee. Part of the predicate, not just a stamp: a notification belonging to another
    /// user is simply not found, so it returns false rather than mutating someone else's row.
    /// </param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <returns>False when no notification matches all three of id, tenant and user.</returns>
    Task<bool> MarkAsReadAsync(Guid notificationId, string tenantId, Guid userId, CancellationToken ct);

    /// <summary>
    /// Marks one notification as unread again: IsRead = false, ReadAt = null. Idempotent.
    /// </summary>
    /// <param name="notificationId">The notification to mark.</param>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="userId">The addressee — part of the predicate, exactly as in <see cref="MarkAsReadAsync"/>.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <returns>False when no notification matches all three of id, tenant and user.</returns>
    Task<bool> MarkAsUnreadAsync(Guid notificationId, string tenantId, Guid userId, CancellationToken ct);

    /// <summary>
    /// Marks the given notifications of one user as read, in one save. Only the user's own UNREAD rows
    /// among <paramref name="notificationIds"/> change; an already-read row keeps its ReadAt, and an id
    /// that is unknown or belongs to someone else is simply not matched.
    /// </summary>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="userId">The addressee (always enforced).</param>
    /// <param name="notificationIds">The ids to mark; the caller bounds the count.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <returns>How many notifications went from unread to read.</returns>
    Task<int> MarkManyAsReadAsync(
        string tenantId, Guid userId, IReadOnlyCollection<Guid> notificationIds, CancellationToken ct);

    /// <summary>
    /// Marks every unread notification of one user as read.
    /// </summary>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="userId">The addressee (always enforced).</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <returns>The number of notifications that were marked as read.</returns>
    Task<int> MarkAllAsReadForUserAsync(string tenantId, Guid userId, CancellationToken ct);

    /// <summary>
    /// Persists a batch of notifications in one round-trip.
    /// </summary>
    /// <param name="notifications">The notifications to insert. An empty sequence is a no-op.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task CreateManyAsync(IEnumerable<Notification> notifications, CancellationToken ct);

    /// <summary>
    /// Whether a notification of the given type already exists for one user within the given period.
    /// </summary>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="userId">The addressee (always enforced).</param>
    /// <param name="type">Exact <see cref="Notification.Type"/> code to look for.</param>
    /// <param name="periodStartUtc">Start of the window, UTC, INCLUSIVE.</param>
    /// <param name="periodEndUtc">End of the window, UTC, EXCLUSIVE.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <remarks>
    /// The per-user mirror of <see cref="IAlertRepository.ExistsByTypeInPeriodAsync"/>, and it exists
    /// for the same reason: a recurring emitter driven from a POLLED endpoint needs to ask "did this
    /// already fire for this user this month?" without loading their history on every poll.
    ///
    /// <para>
    /// The window is half-open on purpose so it composes with BillingUsageSnapshot.PeriodStart /
    /// PeriodEnd, which are the 1st of this month and the 1st of the next. A closed interval would
    /// let a notification written on a boundary satisfy two periods.
    /// </para>
    /// </remarks>
    Task<bool> ExistsByTypeForUserInPeriodAsync(
        string            tenantId,
        Guid              userId,
        string            type,
        DateTime          periodStartUtc,
        DateTime          periodEndUtc,
        CancellationToken ct);

    /// <summary>
    /// Whether a notification of the given type already exists for one user against one SOURCE.
    /// </summary>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="userId">The addressee (always enforced).</param>
    /// <param name="type">Exact <see cref="Notification.Type"/> code to look for.</param>
    /// <param name="sourceId">The <see cref="Notification.SourceId"/> the notification points at.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <remarks>
    /// The counterpart to <see cref="ExistsByTypeForUserInPeriodAsync"/>, for emitters whose events
    /// are tied to a THING rather than to a billing period.
    ///
    /// <para>
    /// The distinction is load-bearing and the two are not interchangeable. A quota notification
    /// asks "did this already fire for this user this month?", because there is one quota per month
    /// and no other identity to key on. An analysis notification asks "did this already fire for
    /// this user about THIS analysis?" — its natural key is (user, type, source). Using the period
    /// method for it would suppress a perfectly legitimate notification about a DIFFERENT analysis
    /// of the same type that happened to fall inside the window, which is a silent loss of exactly
    /// the hand-off the notification exists to deliver.
    /// </para>
    /// </remarks>
    Task<bool> ExistsByTypeForUserAndSourceAsync(
        string            tenantId,
        Guid              userId,
        string            type,
        Guid              sourceId,
        CancellationToken ct);
}
