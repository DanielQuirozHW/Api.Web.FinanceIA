using System.Text;

namespace Api.Web.FinanceIA.Clases.Telemetry;

/// <summary>
/// Removes identifiers from the URLs that reach Application Insights. Several of this API's paths carry a
/// CUIT or a DNI — inbound <c>api/entities/cuit/{cuit}</c> and <c>api/bcra/deudores/{cuit}</c>, outbound
/// BCRA <c>Deudas/{cuit}</c>, LUFE <c>entidades/{cuit}</c>, CASFOG <c>central_deudores/{cuit}</c> — and
/// OpenTelemetry redacts only query-string VALUES, never path segments.
/// </summary>
public static class UrlRedaction
{
    /// <summary>What a path segment holding an identifier is replaced with.</summary>
    public const string Placeholder = "{id}";

    /// <summary>
    /// The shortest run of digits treated as an identifier: a DNI has 7–8 digits, a CUIT 11 (with or
    /// without its dashes, the middle run alone is 8). Shorter numbers — a page, a year, an int id — stay.
    /// </summary>
    public const int MinIdentifierDigits = 7;

    /// <summary>
    /// The path with every segment that holds a run of at least <see cref="MinIdentifierDigits"/> digits
    /// replaced by <see cref="Placeholder"/>. The rest of the path (and its slashes) is kept as is. A GUID
    /// segment that happens to hold such a run is hidden too, which costs nothing.
    /// </summary>
    public static string ScrubPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return path ?? string.Empty;

        var segments = path.Split('/');
        var changed  = false;
        for (var i = 0; i < segments.Length; i++)
        {
            if (HasIdentifier(segments[i]))
            {
                segments[i] = Placeholder;
                changed     = true;
            }
        }

        return changed ? string.Join('/', segments) : path;
    }

    /// <summary>
    /// <c>scheme://host[:port]</c> + <see cref="ScrubPath"/> of the path, WITHOUT the query string or the
    /// fragment (NOSIS sends the CUIT as <c>?Documento=</c>, Identity Platform its key as <c>?key=</c>) and
    /// without any user info. Null for a URI that is not absolute.
    /// </summary>
    public static string? ScrubAbsoluteUrl(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
            return null;

        var text = new StringBuilder(uri.Scheme.Length + uri.Host.Length + uri.AbsolutePath.Length + 10)
            .Append(uri.Scheme)
            .Append(Uri.SchemeDelimiter)
            .Append(uri.Host);

        if (!uri.IsDefaultPort)
            text.Append(':').Append(uri.Port);

        text.Append(ScrubPath(uri.AbsolutePath));
        return text.ToString();
    }

    private static bool HasIdentifier(string segment)
    {
        var run = 0;
        foreach (var c in segment)
        {
            if (c is >= '0' and <= '9')
            {
                if (++run >= MinIdentifierDigits)
                    return true;
            }
            else
            {
                run = 0;
            }
        }

        return false;
    }
}
