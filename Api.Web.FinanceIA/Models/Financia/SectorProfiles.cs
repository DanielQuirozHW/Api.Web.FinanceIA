// Project : Api.Web.FinanceIA
// Created : 2026-06-05

using System.Globalization;
using System.Text;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Canonical sector names and helper methods for sector-aware scoring.
/// Uses <see cref="ScoringDefaults.RatioThresholdKeys"/> — never duplicates them.
/// </summary>
public static class SectorProfiles
{
    public static readonly string[] KnownSectors =
        ["General", "Industria", "Agro", "Servicios", "Construccion", "Comercio"];

    /// <summary>
    /// Normalizes a raw sector string to a canonical sector name.
    /// Returns "General" for null/whitespace or unrecognized values.
    /// Handles accented characters (e.g. "construcción" → "Construccion").
    /// </summary>
    public static string Canonicalize(string? raw)
        => Match(raw) ?? "General";

    /// <summary>
    /// True when <paramref name="raw"/> names one of <see cref="KnownSectors"/> under the same trim,
    /// case and diacritics rules <see cref="Canonicalize"/> applies — "General" itself included. False for
    /// null, whitespace and any unrecognised text, which <see cref="Canonicalize"/> silently maps to
    /// "General". The difference between the two is what the scoring reliability facts call a sector
    /// fallback.
    /// </summary>
    public static bool IsKnown(string? raw)
        => Match(raw) is not null;

    /// <summary>The canonical sector <paramref name="raw"/> names, or null when it names none.</summary>
    private static string? Match(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim();
        var stripped = RemoveDiacritics(trimmed);

        foreach (var sector in KnownSectors)
        {
            if (string.Equals(stripped, sector, StringComparison.OrdinalIgnoreCase))
                return sector;
        }

        return null;
    }

    /// <summary>
    /// Returns the <c>__sector__</c> suffix for a canonical sector name.
    /// Only called when <paramref name="canonicalSector"/> is not "General".
    /// </summary>
    public static string SectorSuffix(string canonicalSector)
        => $"__sector__{canonicalSector}";

    private static string RemoveDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);

        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
