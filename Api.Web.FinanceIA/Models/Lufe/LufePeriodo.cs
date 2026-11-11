// Project : Api.Web.FinanceIA
// Created : 2026-08-03

namespace Api.Web.FinanceIA.Models.Lufe;

/// <summary>
/// Parsing helpers for Lufe "periodo" strings, which arrive in mixed shapes
/// ("2024", "202400", "202412", date-like values).
/// </summary>
public static class LufePeriodo
{
    private const int MinYear = 1990;

    /// <summary>
    /// Extracts the fiscal year from a Lufe periodo string: the first non-overlapping 4-digit
    /// run that falls in range [1990, <paramref name="maxYear"/>], or null when none is present.
    /// Mirrors the portal's parseLufePeriodo (apps/portal/src/lib/lufe-document-match.ts) so the
    /// client and server agree on the year the "Importado" heuristic compares.
    /// </summary>
    public static int? ParseYear(string? periodo, int maxYear)
    {
        if (string.IsNullOrWhiteSpace(periodo)) return null;

        var i = 0;
        while (i + 4 <= periodo.Length)
        {
            if (char.IsDigit(periodo[i]) && char.IsDigit(periodo[i + 1])
                && char.IsDigit(periodo[i + 2]) && char.IsDigit(periodo[i + 3]))
            {
                var year = (periodo[i] - '0') * 1000 + (periodo[i + 1] - '0') * 100
                         + (periodo[i + 2] - '0') * 10 + (periodo[i + 3] - '0');
                if (year >= MinYear && year <= maxYear) return year;
                i += 4; // consume the 4-digit run — matches /\d{4}/g non-overlapping semantics
            }
            else
            {
                i += 1;
            }
        }

        return null;
    }
}
