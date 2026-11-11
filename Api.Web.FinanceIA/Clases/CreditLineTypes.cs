// Project : Api.Web.FinanceIA
// Created : 2026-09-07

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// The SGR One Click credit-line type codes this system accepts, in one place.
/// <para>
/// Extracted from <c>CreditLinesController</c>, which owned the only copy while creating an
/// operation was the only thing that named a type. A decision now carries proposed and confirmed
/// operations that name one too, and two private copies of the same eighteen numbers is how they
/// drift apart — the same reasoning that produced <see cref="Currencies"/>.
/// </para>
/// <para>
/// Deliberately NOT a database CHECK constraint: <c>[dbo].[CreditLineOperations].[Type]</c> has
/// never had one (see the entity's own note), and a value list in the schema would turn adding a
/// line type into a migration on every tenant database. Validation lives here instead.
/// </para>
/// </summary>
public static class CreditLineTypes
{
    /// <summary>
    /// Supported catalogue codes, 0 through 17 inclusive (code -> label is display-only on the
    /// frontend).
    /// <para>
    /// KEEP code 0 ("General"): the frontend no longer offers it as a choice, but the BFF must keep
    /// accepting it so existing stored operations of type 0 still validate and render. Do NOT
    /// remove it.
    /// </para>
    /// </summary>
    public static readonly IReadOnlySet<int> Known =
        new HashSet<int> { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17 };

    /// <summary>True when <paramref name="type"/> is a supported catalogue code.</summary>
    public static bool IsKnown(int type) => Known.Contains(type);

    /// <summary>The accepted range, for error messages.</summary>
    public static string KnownRange => "0 a 17";
}
