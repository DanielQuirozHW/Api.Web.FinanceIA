// Project : Api.Web.FinanceIA
// Created : 2026-11-06

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The totals of a Bienes Personales extraction. The three asset totals are printed and computed; the
/// tax figures are printed only — they depend on exemptions and rules that are not modelled, so nothing
/// derives them. Amounts in the summary's AmountsUnit.
/// </summary>
public sealed record LegajoBienesPersonalesTotalsResponse
{
    /// <summary>Bienes en el país: computed as the sum of the groups' país sums.</summary>
    public required LegajoBienesPersonalesTotalResponse BienesPais { get; init; }

    /// <summary>Bienes en el exterior: computed as the sum of the groups' exterior sums.</summary>
    public required LegajoBienesPersonalesTotalResponse BienesExterior { get; init; }

    /// <summary>
    /// Total de bienes: computed as printed país + printed exterior when both are printed, otherwise as the
    /// sum of every item's fiscal value.
    /// </summary>
    public required LegajoBienesPersonalesTotalResponse TotalBienes { get; init; }

    /// <summary>Bienes exentos, as printed.</summary>
    public decimal? BienesExentos { get; init; }

    /// <summary>Mínimo no imponible, as printed.</summary>
    public decimal? MinimoNoImponible { get; init; }

    /// <summary>Base imponible, as printed.</summary>
    public decimal? BaseImponible { get; init; }

    /// <summary>Impuesto determinado, as printed.</summary>
    public decimal? ImpuestoDeterminado { get; init; }
}
