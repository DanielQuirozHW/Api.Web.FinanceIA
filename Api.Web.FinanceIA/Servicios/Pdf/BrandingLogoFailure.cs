// Project : Api.Web.FinanceIA
// Created : 2026-11-08

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// The reason codes a refused tenant logo is logged with (and cached under for 15 minutes). The guide
/// docs/guides/pdf-branding.md lists them for the DBA; keep the two in step.
/// </summary>
internal static class BrandingLogoFailure
{
    /// <summary>The URL is not https.</summary>
    internal const string Scheme = "scheme";

    /// <summary>The URL names a port other than 443.</summary>
    internal const string Port = "port";

    /// <summary>The URL carries a user name or password.</summary>
    internal const string UserInfo = "userinfo";

    /// <summary>The host resolves to a loopback, private, link-local, CGNAT, metadata or other non-public address.</summary>
    internal const string BlockedAddress = "blocked_address";

    /// <summary>The server answered a redirect; redirects are never followed.</summary>
    internal const string Redirect = "redirect";

    /// <summary>The server answered a status other than 2xx or 3xx.</summary>
    internal const string HttpStatus = "http_status";

    /// <summary>The body is larger than 1 MB.</summary>
    internal const string Oversize = "oversize";

    /// <summary>The bytes are not a PNG or a JPEG (SVG, GIF and WebP included).</summary>
    internal const string Format = "format";

    /// <summary>The image is wider or taller than 4000 px, or has a zero side.</summary>
    internal const string Dimensions = "dimensions";

    /// <summary>The image header was accepted but iText could not decode it.</summary>
    internal const string Undecodable = "undecodable";

    /// <summary>The fetch took longer than 5 seconds.</summary>
    internal const string Timeout = "timeout";

    /// <summary>A DNS, TCP or TLS failure.</summary>
    internal const string Network = "network";
}
