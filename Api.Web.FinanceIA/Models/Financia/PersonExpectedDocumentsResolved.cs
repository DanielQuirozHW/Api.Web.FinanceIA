// Project : Api.Web.FinanceIA
// Created : 2026-10-31

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// A tenant's expected-documents matrix, resolved: every bucket of <see cref="ExpectedDocumentRegimes.All"/>,
/// each from the tenant's row or from the code default.
/// </summary>
public sealed record PersonExpectedDocumentsResolved
{
    /// <summary>One entry per bucket, in <see cref="ExpectedDocumentRegimes.All"/> order.</summary>
    public required IReadOnlyList<PersonExpectedDocumentsForRegime> Regimes { get; init; }

    /// <summary>When the tenant's row was last written; null without a usable row.</summary>
    public DateTime? UpdatedAt { get; init; }

    /// <summary>The code defaults for every bucket.</summary>
    public static PersonExpectedDocumentsResolved Defaults { get; } = new()
    {
        Regimes = ExpectedDocumentRegimes.All
            .Select(regime => new PersonExpectedDocumentsForRegime
            {
                Regime = regime,
                Source = ExpectedDocumentsSources.Default,
                Lines  = LegajoExpectedDocuments.Defaults[regime]
            })
            .ToList()
    };

    /// <summary>The bucket <paramref name="fiscalRegime"/> reads (<see cref="ExpectedDocumentRegimes.Of"/>).</summary>
    public PersonExpectedDocumentsForRegime For(string? fiscalRegime)
    {
        var bucket = ExpectedDocumentRegimes.Of(fiscalRegime);
        return Regimes.First(r => string.Equals(r.Regime, bucket, StringComparison.Ordinal));
    }
}
