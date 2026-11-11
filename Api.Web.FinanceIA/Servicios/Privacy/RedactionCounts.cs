// Project : Api.Web.FinanceIA
// Created : 2026-11-05

namespace Api.Web.FinanceIA.Servicios.Privacy;

/// <summary>How many occurrences <see cref="EntityIdentifierRedactor"/> replaced, by kind. Counts only — never text.</summary>
/// <param name="Names">Occurrences of the company's own name.</param>
/// <param name="Persons">Occurrences of a partner's, authority's or related legal person's name.</param>
/// <param name="Cuits">Occurrences of a CUIT.</param>
public sealed record RedactionCounts(int Names, int Persons, int Cuits)
{
    /// <summary>Nothing replaced.</summary>
    public static RedactionCounts None { get; } = new(0, 0, 0);

    /// <summary>Every replacement, all kinds together.</summary>
    public int Total => Names + Persons + Cuits;

    /// <summary>The sum of this and <paramref name="other"/>.</summary>
    public RedactionCounts Add(RedactionCounts other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new RedactionCounts(Names + other.Names, Persons + other.Persons, Cuits + other.Cuits);
    }
}
