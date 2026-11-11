// Project : Api.Web.FinanceIA
// Created : 2026-11-08

using System.Net;
using System.Net.Sockets;

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// Which addresses the tenant-logo fetch may connect to: public unicast only. The logo URL is DBA-seeded, but it is
/// still a URL the server fetches on a user's request, so every address that could reach the App Service's own
/// network, the platform metadata endpoint or a neighbour is refused — IPv4 and IPv6, including every IPv6 form
/// that embeds an IPv4 address.
/// </summary>
internal static class BrandingLogoAddressPolicy
{
    /// <summary>True when <paramref name="address"/> is a public unicast address the fetch may connect to.</summary>
    internal static bool IsAllowed(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        byte[] b = address.GetAddressBytes();

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork   => IsAllowedV4(b),
            AddressFamily.InterNetworkV6 => IsAllowedV6(b),
            _                            => false
        };
    }

    private static bool IsAllowedV4(ReadOnlySpan<byte> b)
    {
        if (b[0] == 0)                                    return false;   // 0.0.0.0/8 "this network"
        if (b[0] == 10)                                   return false;   // 10/8 private
        if (b[0] == 127)                                  return false;   // 127/8 loopback
        if (b[0] == 100 && (b[1] & 0xC0) == 64)           return false;   // 100.64/10 CGNAT
        if (b[0] == 169 && b[1] == 254)                   return false;   // 169.254/16 link-local, 169.254.169.254 metadata
        if (b[0] == 172 && (b[1] & 0xF0) == 16)           return false;   // 172.16/12 private
        if (b[0] == 192 && b[1] == 0 && b[2] == 0)        return false;   // 192.0.0/24 IETF protocol assignments
        if (b[0] == 192 && b[1] == 168)                   return false;   // 192.168/16 private
        if (b[0] == 198 && (b[1] & 0xFE) == 18)           return false;   // 198.18/15 benchmarking
        if (b[0] >= 224)                                  return false;   // 224/4 multicast, 240/4 reserved, broadcast
        return true;
    }

    private static bool IsAllowedV6(ReadOnlySpan<byte> b)
    {
        // ::/96 — the unspecified address (::), loopback (::1) and the deprecated IPv4-compatible form all end in an
        // IPv4 address the v4 rules already refuse or allow (:: → 0.0.0.0, ::1 → 0.0.0.1, both in 0/8).
        if (b[..12].IndexOfAnyExcept((byte)0) < 0)
            return IsAllowedV4(b[12..]);

        // ::ffff:0:0/96 IPv4-mapped.
        if (b[..10].IndexOfAnyExcept((byte)0) < 0 && b[10] == 0xFF && b[11] == 0xFF)
            return IsAllowedV4(b[12..]);

        // 64:ff9b::/96 NAT64 well-known prefix.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4..12].IndexOfAnyExcept((byte)0) < 0)
            return IsAllowedV4(b[12..]);

        // 2002::/16 6to4: the IPv4 address is bytes 2..5.
        if (b[0] == 0x20 && b[1] == 0x02)
            return IsAllowedV4(b[2..6]);

        if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return false;   // fe80::/10 link-local
        if (b[0] == 0xFE && (b[1] & 0xC0) == 0xC0) return false;   // fec0::/10 site-local (deprecated)
        if ((b[0] & 0xFE) == 0xFC)                 return false;   // fc00::/7 unique-local
        if (b[0] == 0xFF)                          return false;   // ff00::/8 multicast
        return true;
    }
}
