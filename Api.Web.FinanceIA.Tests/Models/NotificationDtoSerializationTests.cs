// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-31

using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Models;

/// <summary>
/// Locks the WIRE CONTRACT of the notifications endpoints, which the Portal is built against.
///
/// <para>
/// These property names are not an implementation detail — a rename here silently breaks the
/// front end at runtime, with no compiler anywhere to catch it. The casing is camelCase because
/// <c>Program.cs</c> calls <c>AddControllers().AddJsonOptions(...)</c> and adds only the two UTC
/// DateTime converters, setting no <c>PropertyNamingPolicy</c>; MVC therefore builds its options
/// from <see cref="JsonSerializerDefaults.Web"/>, which is what this test reproduces.
/// </para>
/// </summary>
public sealed class NotificationDtoSerializationTests
{
    // The same starting point AddControllers() uses, so this test tracks the real pipeline rather
    // than a guess about it.
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    [Fact]
    public void NotificationDto_SerialisesEveryFieldInCamelCase()
    {
        var dto = new NotificationDto
        {
            NotificationId = Guid.NewGuid(),
            Type           = "BILLING_ANALYSIS_QUOTA_80",
            Title          = "Consumo del plan al 80%",
            Body           = "Usaste el 82% de los análisis de tu plan (100 análisis) este mes.",
            SourceType     = "Alert",
            SourceId       = Guid.NewGuid(),
            IsRead         = false,
            ReadAt         = null,
            CreatedAt      = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc),
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dto, WebDefaults));
        var actual = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        Assert.Equal(
            [
                "notificationId", "type", "title", "body",
                "sourceType", "sourceId", "isRead", "readAt", "createdAt"
            ],
            actual);
    }

    /// <summary>
    /// Nullable fields must be PRESENT and null, not omitted: the Portal reads them positionally
    /// off a typed model, and a missing key is not the same as an explicit null there.
    /// </summary>
    [Fact]
    public void NotificationDto_KeepsNullableFieldsPresentWhenNull()
    {
        var dto = new NotificationDto
        {
            NotificationId = Guid.NewGuid(),
            Type           = "BILLING_ANALYSIS_QUOTA_80",
            Title          = "Consumo del plan al 80%",
            Body           = null,
            SourceType     = null,
            SourceId       = null,
            IsRead         = true,
            ReadAt         = null,
            CreatedAt      = DateTime.UtcNow,
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dto, WebDefaults));

        foreach (var field in new[] { "body", "sourceType", "sourceId", "readAt" })
        {
            Assert.True(doc.RootElement.TryGetProperty(field, out var value), $"missing '{field}'");
            Assert.Equal(JsonValueKind.Null, value.ValueKind);
        }
    }

    /// <summary>
    /// The listing envelope is exactly { notifications, unreadCount, nextCursor, totalCount }, in that
    /// order: the first two are the original contract (a Portal that predates paging reads only them),
    /// the last two were added for paging. nextCursor is PRESENT and null on the last page, never omitted.
    /// </summary>
    [Fact]
    public void NotificationsResponseDto_SerialisesTheFourContractKeys()
    {
        var response = new NotificationsResponseDto
        {
            Notifications = [],
            UnreadCount   = 3,
            NextCursor    = null,
            TotalCount    = 98,
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(response, WebDefaults));

        Assert.Equal(
            ["notifications", "unreadCount", "nextCursor", "totalCount"],
            doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("notifications").ValueKind);
        Assert.Equal(3, doc.RootElement.GetProperty("unreadCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("nextCursor").ValueKind);
        Assert.Equal(98, doc.RootElement.GetProperty("totalCount").GetInt32());
    }

    /// <summary>GET /api/notifications/unread-count returns exactly { "unreadCount": n }.</summary>
    [Fact]
    public void UnreadCountResponseDto_SerialisesAsUnreadCount()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new UnreadCountResponseDto { UnreadCount = 4 }, WebDefaults));

        Assert.Equal(["unreadCount"], doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(4, doc.RootElement.GetProperty("unreadCount").GetInt32());
    }

    /// <summary>POST /api/notifications/mark-read reads { "ids": [...] } in camelCase.</summary>
    [Fact]
    public void MarkNotificationsReadRequest_DeserialisesIds()
    {
        var id = Guid.NewGuid();

        var request = JsonSerializer.Deserialize<MarkNotificationsReadRequest>($"{{\"ids\":[\"{id}\"]}}", WebDefaults);

        Assert.Equal([id], request!.Ids!);
    }

    /// <summary>
    /// mark-all-read and mark-read return { "updated": n }. The controller ships an anonymous object, which is
    /// serialised by the same options, so the key is camelCase too.
    /// </summary>
    [Fact]
    public void MarkAllReadResponse_SerialisesAsUpdated()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { updated = 7 }, WebDefaults));

        Assert.Equal(["updated"], doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(7, doc.RootElement.GetProperty("updated").GetInt32());
    }
}
