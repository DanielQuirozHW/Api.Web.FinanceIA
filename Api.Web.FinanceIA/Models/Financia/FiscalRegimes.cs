// Project : Api.Web.FinanceIA
// Created : 2026-09-27

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The fiscal-regime values of [dbo].[Entities].[FiscalRegime] and [dbo].[PersonLegajos].[FiscalRegime]
/// — the vocabulary of CK_Entities_FiscalRegime and CK_PersonLegajos_FiscalRegime (DDL 069).
/// </summary>
/// <remarks>
/// Derived from ARCA's getPersona_v2 answer by <c>ArcaConstanciaService.ResolveFiscalRegime</c>.
/// <see cref="Desconocido"/> is a real value, not an error: it is what the API stores when ARCA's
/// answer does not determine the regime, and the legajo's column default.
///
/// <para>
/// Widening this list takes a numbered DDL on every business database, as DDL 060 did for
/// CK_Analyses_Status — the constraints in 069 enumerate exactly <see cref="Known"/>.
/// </para>
/// </remarks>
public static class FiscalRegimes
{
    /// <summary>Adherido al Régimen Simplificado (monotributo). Carries a category A–K.</summary>
    public const string Monotributo = "Monotributo";

    /// <summary>IVA responsable inscripto.</summary>
    public const string ResponsableInscripto = "ResponsableInscripto";

    /// <summary>IVA exento.</summary>
    public const string Exento = "Exento";

    /// <summary>ARCA's answer did not determine the regime (or there was no answer).</summary>
    public const string Desconocido = "Desconocido";

    /// <summary>Every value the two CHECK constraints admit.</summary>
    public static readonly IReadOnlySet<string> Known =
        new HashSet<string>(StringComparer.Ordinal) { Monotributo, ResponsableInscripto, Exento, Desconocido };

    /// <summary>True when <paramref name="regime"/> is exactly one of <see cref="Known"/>.</summary>
    public static bool IsKnown(string? regime)
        => !string.IsNullOrWhiteSpace(regime) && Known.Contains(regime);

    /// <summary>
    /// True when <paramref name="regime"/> carries information: a known value other than
    /// <see cref="Desconocido"/>. Used so that a request or a partial ARCA answer never downgrades
    /// a regime the system already knows.
    /// </summary>
    public static bool IsDeterminate(string? regime)
        => IsKnown(regime) && !string.Equals(regime, Desconocido, StringComparison.Ordinal);

    /// <summary>
    /// The monotributo category as the single upper-case letter A–K, or null when
    /// <paramref name="raw"/> is blank or anything else. Accepts the bare letter in either case
    /// ("h" → "H"); anything longer is rejected here — extracting the letter out of ARCA's
    /// <c>descripcionCategoria</c> is the parser's job.
    /// </summary>
    public static string? NormalizeMonotributoCategory(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var trimmed = raw.Trim();
        if (trimmed.Length != 1) return null;

        var letter = char.ToUpperInvariant(trimmed[0]);
        return letter is >= 'A' and <= 'K' ? letter.ToString() : null;
    }
}
