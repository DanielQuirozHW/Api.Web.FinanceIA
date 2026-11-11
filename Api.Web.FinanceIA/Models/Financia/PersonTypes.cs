// Project : Api.Web.FinanceIA
// Created : 2026-09-27

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The [dbo].[Entities].[PersonType] values — the vocabulary of CK_Entities_PersonType (DDL 069).
/// </summary>
/// <remarks>
/// Named in ONE place for the reason <see cref="AnalysisStatuses"/> gives: the create path, the PUT
/// resync, the ARCA lookup and the legajo rules all branch on these, and the compiler cannot see a
/// typo inside a string literal. The constraint lives on every business database and takes a
/// numbered DDL to widen, so adding a value here is not enough on its own.
///
/// <para>
/// Binary and structural rather than an evolving catalogue: CK_Entities_PersonLegajo_Humana hangs
/// the legajo link off <see cref="Humana"/>. That is why this vocabulary IS a CHECK, unlike the
/// API-validated lists 036, 046, 052 and 067 chose for (see <see cref="Clases.Currencies"/>).
/// </para>
/// </remarks>
public static class PersonTypes
{
    /// <summary>A company (persona jurídica). The column default, and every row before DDL 069.</summary>
    public const string Juridica = "Juridica";

    /// <summary>A natural person (persona humana). Carries a PersonLegajos row.</summary>
    public const string Humana = "Humana";

    /// <summary>Every value CK_Entities_PersonType admits.</summary>
    public static readonly IReadOnlySet<string> Known =
        new HashSet<string>(StringComparer.Ordinal) { Juridica, Humana };

    /// <summary>True when <paramref name="personType"/> is exactly one of <see cref="Known"/>.</summary>
    public static bool IsKnown(string? personType)
        => !string.IsNullOrWhiteSpace(personType) && Known.Contains(personType);

    /// <summary>
    /// Maps a client-supplied value onto its canonical spelling, ignoring case ("humana" →
    /// <see cref="Humana"/>). Null when the value is blank or not a person type at all.
    /// </summary>
    public static string? Canonicalize(string? personType)
    {
        if (string.IsNullOrWhiteSpace(personType)) return null;

        var trimmed = personType.Trim();
        return Known.FirstOrDefault(known => string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase));
    }
}
