// Project : Api.Web.FinanceIA
// Created : 2026-08-31

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// DTO for a single notification returned by the notifications endpoint.
/// </summary>
/// <remarks>
/// Serialised in camelCase (notificationId, sourceType, isRead, ...): Program.cs configures
/// AddJsonOptions without a PropertyNamingPolicy, so the ASP.NET Core web default applies —
/// the same casing every other controller in this API emits.
///
/// <para>
/// TenantId is deliberately absent. It is implicit in the caller's own tenant context, and unlike
/// <see cref="AlertDto"/> — which carries it for historical reasons — there is no reason to echo
/// it back on a per-user resource.
/// </para>
/// </remarks>
public sealed class NotificationDto
{
    public Guid NotificationId { get; init; }

    /// <summary>Type code, e.g. BILLING_ANALYSIS_QUOTA_80.</summary>
    public string Type { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string? Body { get; init; }

    /// <summary>What produced this notification, e.g. "Alert". Null when it has no source.</summary>
    public string? SourceType { get; init; }

    /// <summary>Identifier within <see cref="SourceType"/>, e.g. the AlertId.</summary>
    public Guid? SourceId { get; init; }

    public bool IsRead { get; init; }

    public DateTime? ReadAt { get; init; }

    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Response envelope for the notifications listing endpoint.
/// </summary>
public sealed class NotificationsResponseDto
{
    /// <summary>One page of the current user's notifications, most recent first.</summary>
    public List<NotificationDto> Notifications { get; init; } = new();

    /// <summary>
    /// How many of the user's notifications are unread — ALL of them, not just this page and not just
    /// the current filter. It drives the Portal badge, so it never depends on what was asked for.
    /// </summary>
    public int UnreadCount { get; init; }

    /// <summary>
    /// Opaque cursor for the next page (pass it back as <c>?cursor=</c>). Null on the last page.
    /// Serialised as <c>null</c>, not omitted.
    /// </summary>
    public string? NextCursor { get; init; }

    /// <summary>How many notifications match the current filters, across every page.</summary>
    public int TotalCount { get; init; }
}

/// <summary>Response of GET /api/notifications/unread-count.</summary>
public sealed class UnreadCountResponseDto
{
    /// <summary>The user's total unread notifications — the same number the listing's unreadCount carries.</summary>
    public int UnreadCount { get; init; }
}

/// <summary>Body of POST /api/notifications/mark-read.</summary>
public sealed class MarkNotificationsReadRequest
{
    /// <summary>The notifications to mark as read; 1..100 ids. Ids that are not the caller's are ignored.</summary>
    public List<Guid>? Ids { get; init; }
}

/// <summary>
/// The validated filters and page position of one notifications listing, as the repository takes them.
/// Every field is optional except <see cref="Limit"/>; the tenant and the user are separate arguments
/// and are never part of a query object.
/// </summary>
public sealed record NotificationQuery
{
    /// <summary>Page size, already validated to 1..100.</summary>
    public int Limit { get; init; } = 50;

    /// <summary>CreatedAt of the last row of the previous page; null on the first page.</summary>
    public DateTime? AfterCreatedAt { get; init; }

    /// <summary>NotificationId of the last row of the previous page; set together with <see cref="AfterCreatedAt"/>.</summary>
    public Guid? AfterNotificationId { get; init; }

    /// <summary>True → unread only.</summary>
    public bool UnreadOnly { get; init; }

    /// <summary>Exact Type code.</summary>
    public string? Type { get; init; }

    /// <summary>Exact SourceType.</summary>
    public string? SourceType { get; init; }

    /// <summary>Exact SourceId (only with <see cref="SourceType"/>).</summary>
    public Guid? SourceId { get; init; }

    /// <summary>The PyME the notification is about (via its Analysis result or its run's document).</summary>
    public Guid? EntityId { get; init; }

    /// <summary>CreatedAt lower bound, UTC, INCLUSIVE.</summary>
    public DateTime? FromUtc { get; init; }

    /// <summary>CreatedAt upper bound, UTC, EXCLUSIVE.</summary>
    public DateTime? ToUtc { get; init; }
}

/// <summary>One page of a notifications listing.</summary>
/// <param name="Items">At most <see cref="NotificationQuery.Limit"/> rows, newest first.</param>
/// <param name="HasMore">Whether at least one more row follows the last one.</param>
public sealed record NotificationPage(IReadOnlyList<Notification> Items, bool HasMore);
