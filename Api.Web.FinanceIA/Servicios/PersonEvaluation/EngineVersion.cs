// Project : Api.Web.FinanceIA
// Created : 2026-10-31

namespace Api.Web.FinanceIA.Servicios.PersonEvaluation;

/// <summary>
/// Orders persona humana engine versions ("0.9", "0.10") and the analysis PromptVersion they suffix
/// ("pf-0.9", "pf-0.10") as dotted NUMBERS: "0.10" comes after "0.9", which ordinal text order gets wrong.
/// </summary>
/// <remarks>
/// A leading non-numeric prefix ("pf-") is compared ordinally first; then each dot-separated segment as an
/// integer, a missing segment reading as 0 ("1" = "1.0"). A value whose segments are not all integers falls
/// back to ordinal order after every well-formed one. Null and empty sort first.
/// </remarks>
public sealed class EngineVersion : IComparer<string?>
{
    /// <summary>The shared instance.</summary>
    public static EngineVersion Comparer { get; } = new();

    private EngineVersion()
    {
    }

    /// <summary>Negative when <paramref name="x"/> is the earlier version, zero when equal, positive when later.</summary>
    public static int CompareVersions(string? x, string? y) => Comparer.Compare(x, y);

    /// <inheritdoc />
    public int Compare(string? x, string? y)
    {
        if (string.IsNullOrEmpty(x) || string.IsNullOrEmpty(y))
            return (string.IsNullOrEmpty(x) ? 0 : 1) - (string.IsNullOrEmpty(y) ? 0 : 1);

        var (xPrefix, xSegments) = Split(x);
        var (yPrefix, ySegments) = Split(y);

        var byPrefix = string.CompareOrdinal(xPrefix, yPrefix);
        if (byPrefix != 0)
            return byPrefix;

        if (xSegments is null || ySegments is null)
        {
            if (xSegments is null && ySegments is null)
                return string.CompareOrdinal(x, y);

            return xSegments is null ? 1 : -1;
        }

        for (var i = 0; i < Math.Max(xSegments.Length, ySegments.Length); i++)
        {
            var a = i < xSegments.Length ? xSegments[i] : 0;
            var b = i < ySegments.Length ? ySegments[i] : 0;
            if (a != b)
                return a.CompareTo(b);
        }

        return 0;
    }

    /// <summary>The non-numeric prefix and the integer segments; segments null when any is not an integer.</summary>
    private static (string Prefix, long[]? Segments) Split(string value)
    {
        var start = 0;
        while (start < value.Length && !char.IsAsciiDigit(value[start]))
            start++;

        var prefix = value[..start];
        var parts  = value[start..].Split('.');
        var segments = new long[parts.Length];

        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit) || !long.TryParse(parts[i], out segments[i]))
                return (prefix, null);
        }

        return (prefix, segments);
    }
}
