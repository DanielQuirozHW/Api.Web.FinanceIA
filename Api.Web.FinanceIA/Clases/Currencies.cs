// Project : Api.Web.FinanceIA
// Created : 2026-09-04

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// The ISO 4217 currency codes this system accepts, in one place.
/// <para>
/// Extracted from <c>CreditLinesController</c>, which owned the only copy while a cupo was the
/// only thing carrying an amount. A decision amount and a per-user approval limit now carry one
/// too, and three private copies of the same three strings is how they drift apart.
/// </para>
/// <para>
/// Deliberately NOT a database CHECK constraint, following the reasoning recorded in DDL scripts
/// 036, 046 and 052: the catalogue is expected to evolve, and a value list in the schema turns
/// adding a currency into a migration on every tenant database. Validation lives here instead.
/// </para>
/// </summary>
public static class Currencies
{
    /// <summary>
    /// Supported ISO 4217 codes. Ordinal comparison, matching how the codes are stored
    /// (<c>NVARCHAR(3)</c>) and compared everywhere else in this codebase.
    /// </summary>
    public static readonly IReadOnlySet<string> Known =
        new HashSet<string>(StringComparer.Ordinal) { "ARS", "USD", "EUR" };

    /// <summary>
    /// True when <paramref name="currency"/> is a supported code. Null, blank and unknown codes
    /// are all false, so a caller needs no separate null check.
    /// </summary>
    public static bool IsKnown(string? currency)
        => !string.IsNullOrWhiteSpace(currency) && Known.Contains(currency);

    /// <summary>Comma-separated list of the supported codes, for error messages.</summary>
    public static string KnownList => string.Join(", ", Known);
}
