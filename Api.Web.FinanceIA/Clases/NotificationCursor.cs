// Project : Api.Web.FinanceIA
// Created : 2026-11-06

using System.Globalization;
using System.Text;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// The opaque keyset cursor of GET /api/notifications: the (CreatedAt, NotificationId) of the last row
/// of a page, which the next page continues strictly after under ORDER BY CreatedAt DESC,
/// NotificationId DESC.
/// </summary>
/// <remarks>
/// <para>
/// Wire form: base64url (no padding) of <c>v1|{CreatedAt.Ticks}|{NotificationId:N}</c>. Ticks, not an
/// ISO string: <c>datetime2(7)</c> has 100 ns precision, exactly one tick, so the round-trip is
/// lossless and a row is never skipped or repeated because a formatted timestamp lost a digit.
/// </para>
/// <para>
/// OPAQUE to the caller, and deliberately not signed: it carries no tenant, no user and no filter, only
/// a position. Every query is still scoped by the caller's own TenantId and UserId, so a forged cursor
/// can only move the caller around their own inbox. It carries no filters either — reusing a cursor with
/// different filters simply continues the new filter from that position.
/// </para>
/// </remarks>
public static class NotificationCursor
{
    private const string Version = "v1";

    /// <summary>Encodes the position of <paramref name="createdAt"/> / <paramref name="notificationId"/>.</summary>
    public static string Encode(DateTime createdAt, Guid notificationId)
    {
        var raw = string.Create(
            CultureInfo.InvariantCulture,
            $"{Version}|{createdAt.Ticks}|{notificationId:N}");

        return Convert.ToBase64String(Encoding.ASCII.GetBytes(raw))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// Decodes a cursor produced by <see cref="Encode"/>. False for anything else — wrong version,
    /// invalid base64url, ticks out of range, a malformed id — never an exception.
    /// </summary>
    public static bool TryDecode(string? cursor, out DateTime createdAt, out Guid notificationId)
    {
        createdAt      = default;
        notificationId = default;

        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 128) return false;

        var base64 = cursor.Replace('-', '+').Replace('_', '/');
        base64 = (base64.Length % 4) switch
        {
            2 => base64 + "==",
            3 => base64 + "=",
            0 => base64,
            _ => string.Empty,
        };
        if (base64.Length == 0) return false;

        string raw;
        try
        {
            raw = Encoding.ASCII.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = raw.Split('|');
        if (parts.Length != 3 || parts[0] != Version) return false;

        if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks < DateTime.MinValue.Ticks
            || ticks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        if (!Guid.TryParseExact(parts[2], "N", out var id)) return false;

        // Unspecified, like every datetime2 EF materialises: the comparison in SQL is kind-agnostic, and
        // the in-memory provider compares ticks.
        createdAt      = new DateTime(ticks, DateTimeKind.Unspecified);
        notificationId = id;
        return true;
    }
}
