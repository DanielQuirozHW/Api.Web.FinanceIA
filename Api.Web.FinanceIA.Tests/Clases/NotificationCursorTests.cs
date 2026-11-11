// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-06

using System.Text;
using Api.Web.FinanceIA.Clases;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// The keyset cursor of GET /api/notifications: lossless round-trip of a datetime2(7) instant and a
/// NotificationId, URL-safe, and a clean "invalid" for anything it did not produce.
/// </summary>
public sealed class NotificationCursorTests
{
    [Fact]
    public void RoundTrip_IsLossless_ToTheTick()
    {
        var createdAt = new DateTime(2026, 11, 6, 14, 3, 22, DateTimeKind.Utc).AddTicks(1_234_567);
        var id = Guid.NewGuid();

        var cursor = NotificationCursor.Encode(createdAt, id);

        Assert.True(NotificationCursor.TryDecode(cursor, out var decodedAt, out var decodedId));
        Assert.Equal(createdAt.Ticks, decodedAt.Ticks);
        Assert.Equal(id, decodedId);
    }

    [Fact]
    public void Encode_IsUrlSafe()
    {
        for (var i = 0; i < 200; i++)
        {
            var cursor = NotificationCursor.Encode(DateTime.UtcNow.AddTicks(i * 7919), Guid.NewGuid());
            Assert.DoesNotContain('+', cursor);
            Assert.DoesNotContain('/', cursor);
            Assert.DoesNotContain('=', cursor);
        }
    }

    public static TheoryData<string?> Invalid() => new()
    {
        null,
        "",
        "   ",
        "not-a-cursor",
        "%%%%",
        B64("v2|638000000000000000|" + Guid.NewGuid().ToString("N")),   // unknown version
        B64("v1|abc|" + Guid.NewGuid().ToString("N")),                  // ticks not a number
        B64("v1|-5|" + Guid.NewGuid().ToString("N")),                   // negative ticks
        B64("v1|99999999999999999999|" + Guid.NewGuid().ToString("N")), // ticks overflow
        B64("v1|638000000000000000|not-a-guid"),
        B64("v1|638000000000000000"),                                   // missing part
        new string('A', 200),                                           // over-long
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void TryDecode_RejectsAnythingItDidNotProduce(string? cursor)
    {
        Assert.False(NotificationCursor.TryDecode(cursor, out _, out _));
    }

    private static string B64(string raw)
        => Convert.ToBase64String(Encoding.ASCII.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
