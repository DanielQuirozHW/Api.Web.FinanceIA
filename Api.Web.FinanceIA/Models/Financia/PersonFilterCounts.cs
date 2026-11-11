// Project : Api.Web.FinanceIA
// Created : 2026-10-30

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>How many of a persona humana evaluation's pre-filters ended in each <see cref="PersonFilterOutcomes"/>.</summary>
public sealed record PersonFilterCounts(int Ok, int Alerta, int Rechazo, int SinDatos)
{
    /// <summary>The counts of <paramref name="filters"/>.</summary>
    public static PersonFilterCounts Of(IReadOnlyList<PersonFilterResult> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);

        return new PersonFilterCounts(
            filters.Count(f => f.Outcome == PersonFilterOutcomes.Ok),
            filters.Count(f => f.Outcome == PersonFilterOutcomes.Alerta),
            filters.Count(f => f.Outcome == PersonFilterOutcomes.Rechazo),
            filters.Count(f => f.Outcome == PersonFilterOutcomes.SinDatos));
    }
}
