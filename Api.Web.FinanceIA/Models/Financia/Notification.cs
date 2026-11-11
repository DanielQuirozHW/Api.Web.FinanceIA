// Project : Api.Web.FinanceIA
// Created : 2026-08-31

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[Notifications].
/// Represents a PER-USER notification: "something happened, go look".
/// </summary>
/// <remarks>
/// Deliberately distinct from <see cref="Alert"/>. An alert is per-TENANT — every user of the
/// tenant sees the same row and a read by one marks it read for all. A notification is addressed
/// to exactly one <see cref="UserId"/>, which is what makes per-user read state possible.
/// A tenant-wide event becomes N notifications, one per user; there is no broadcast row.
///
/// <para>
/// The shape is generic on purpose. <see cref="Type"/> is an open code and
/// <see cref="SourceType"/> / <see cref="SourceId"/> are an untyped pointer back at whatever
/// produced the row, so a new emitter is a new Type value rather than a schema change. In v1 the
/// only emitter is the billing quota alert, which writes SourceType = "Alert" and the AlertId.
/// </para>
/// </remarks>
public sealed class Notification
{
    /// <summary>Primary key. Supplied by the application — see NotificationConfiguration.</summary>
    public Guid NotificationId { get; set; }

    /// <summary>Tenant that owns this notification. Firebase tenant slug, not a GUID.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>User this notification is addressed to. The key difference vs <see cref="Alert"/>.</summary>
    public Guid UserId { get; set; }

    /// <summary>Notification type code (e.g. BILLING_ANALYSIS_QUOTA_80).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Human-readable title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Longer body copy. Null when the title says everything.</summary>
    public string? Body { get; set; }

    /// <summary>What produced this notification (e.g. "Alert"). Null when it has no source.</summary>
    public string? SourceType { get; set; }

    /// <summary>Identifier within <see cref="SourceType"/> (e.g. the AlertId). Null when it has no source.</summary>
    public Guid? SourceId { get; set; }

    /// <summary>Whether the addressed user has read this notification.</summary>
    public bool IsRead { get; set; }

    /// <summary>UTC timestamp when the notification was marked as read. Null while unread.</summary>
    public DateTime? ReadAt { get; set; }

    /// <summary>UTC timestamp when the notification was created.</summary>
    public DateTime CreatedAt { get; set; }
}
