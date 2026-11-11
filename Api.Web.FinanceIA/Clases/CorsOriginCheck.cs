// Project : Api.Web.FinanceIA
// Created : 2026-11-01
// Modified: 2026-11-01

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Spots <c>Cors:AllowedOrigins</c> entries that are not a bare origin (<c>scheme://host[:port]</c>).
/// </summary>
/// <remarks>
/// A browser sends <c>Origin: https://host</c>, with no path and no trailing slash, and the CORS
/// middleware compares it as a string, so <c>https://host/</c> never matches it. The entry looks right in
/// configuration and fails silently, which is how <c>https://financia-portal.vercel.app/</c> sat in
/// appsettings.json without matching anything. Program.cs logs a startup Warning from this check. The
/// CORS policy itself is not changed: a flagged entry is still passed to <c>WithOrigins</c> as is.
/// </remarks>
internal static class CorsOriginCheck
{
    /// <summary>
    /// The indexes of <paramref name="origins"/> that are not a bare origin: not an absolute URI, or
    /// one carrying a path (a trailing slash included), a query, a fragment or user info, or written
    /// in a way a browser would never send (an explicit default port, for example).
    /// </summary>
    /// <param name="origins">The configured origins, in configuration order.</param>
    /// <returns>The offending indexes, ascending; empty when every entry is a bare origin.</returns>
    public static IReadOnlyList<int> FindNonOriginIndexes(IReadOnlyList<string> origins)
    {
        ArgumentNullException.ThrowIfNull(origins);

        var offending = new List<int>();

        for (var i = 0; i < origins.Count; i++)
        {
            if (!IsBareOrigin(origins[i]))
            {
                offending.Add(i);
            }
        }

        return offending;
    }

    private static bool IsBareOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // GetLeftPart(Authority) is the origin the browser would send for this URI. Anything the
        // configured value carries beyond it (a path, "/", a query, a default port) cannot match.
        return string.Equals(uri.GetLeftPart(UriPartial.Authority), trimmed, StringComparison.OrdinalIgnoreCase);
    }
}
