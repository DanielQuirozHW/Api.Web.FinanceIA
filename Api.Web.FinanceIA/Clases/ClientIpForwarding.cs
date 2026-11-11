// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using Microsoft.AspNetCore.HttpOverrides;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// The single place the client IP is resolved. Program.cs runs <c>UseForwardedHeaders</c> with these
/// options first in the pipeline; everything after it reads
/// <c>HttpContext.Connection.RemoteIpAddress</c> and never a forwarding header.
/// </summary>
/// <remarks>
/// <para>
/// Request path in production: browser → Vercel (the Portal's next.config.ts rewrites
/// <c>/api/:path*</c> to the BFF; the browser never calls App Service directly) → App Service front
/// end → this app (Windows, in-process; in-process hosting does not add forwarded-headers handling
/// by itself). Vercel OVERWRITES any client-supplied X-Forwarded-For with the client's IP and passes
/// it to an external rewrite destination (https://vercel.com/docs/headers/request-headers,
/// https://vercel.com/docs/rewrites); the front end then APPENDS the address it accepted the
/// connection from. The app therefore sees <c>&lt;client&gt;, &lt;vercel egress&gt;:&lt;port&gt;</c>.
/// </para>
/// <para>
/// <see cref="ForwardedHeadersOptions.ForwardLimit"/> is the number of hops trusted from the right,
/// read from <see cref="TrustedHopsKey"/>. The default, 2 (front end + Vercel), resolves the user;
/// 1 would resolve Vercel's egress IP and put every user in one per-IP bucket. Use 1 only for a
/// deployment served straight from Azure with no Vercel in front. Each further proxy (Front Door,
/// Application Gateway) adds a hop.
/// </para>
/// <para>
/// KnownNetworks/KnownProxies are cleared because neither the App Service front ends nor Vercel have
/// a stable published address list, and with the default (loopback only) the middleware would
/// ignore the header from every real peer.
/// </para>
/// <para>
/// Residual risk: the App Service URL is public. A caller who bypasses Vercel and sends
/// <c>X-Forwarded-For: &lt;spoof&gt;</c> straight to it arrives as <c>&lt;spoof&gt;, &lt;caller&gt;:&lt;port&gt;</c>,
/// and the second trusted hop is then attacker-written: rotating it buys a fresh per-IP budget and
/// writes a fake IP to the audit tables. Only the first hop (appended by the front end) is never
/// spoofable. Planned follow-up, not implemented here: the Portal's Next middleware adds a
/// shared-secret header and the BFF trusts the second hop only when that header is present and valid.
/// </para>
/// </remarks>
public static class ClientIpForwarding
{
    /// <summary>Configuration key for the number of trusted forwarding hops.</summary>
    public const string TrustedHopsKey = "ForwardedHeaders:TrustedHops";

    /// <summary>App Service front end + Vercel.</summary>
    public const int DefaultTrustedHops = 2;

    /// <summary>
    /// Reads <see cref="TrustedHopsKey"/>, defaulting to <see cref="DefaultTrustedHops"/>. Called once
    /// at startup so a bad value stops the app there instead of failing every request.
    /// </summary>
    /// <exception cref="InvalidOperationException">The configured value is below 1.</exception>
    public static int ReadTrustedHops(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var trustedHops = configuration.GetValue(TrustedHopsKey, DefaultTrustedHops);
        if (trustedHops < 1)
        {
            throw new InvalidOperationException($"{TrustedHopsKey} must be 1 or greater.");
        }

        return trustedHops;
    }

    /// <summary>Applies the forwarding rules described on <see cref="ClientIpForwarding"/>.</summary>
    public static void Configure(ForwardedHeadersOptions options, int trustedHops)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(trustedHops, 1);

        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit     = trustedHops;
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
    }
}
