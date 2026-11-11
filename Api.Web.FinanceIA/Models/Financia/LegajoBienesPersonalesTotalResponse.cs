// Project : Api.Web.FinanceIA
// Created : 2026-11-06

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>A Bienes Personales total as printed and as computed from the items. Amounts in the summary's AmountsUnit.</summary>
public sealed record LegajoBienesPersonalesTotalResponse
{
    /// <summary>JSON Pointer of the printed field (e.g. <c>/totals/totalBienes</c>).</summary>
    public required string FieldPath { get; init; }

    /// <summary>The extracted (effective) value, or null when the document does not print it.</summary>
    public decimal? Printed { get; init; }

    /// <summary>The value computed from the extraction, or null when nothing can be computed (or on overflow).</summary>
    public decimal? Computed { get; init; }

    /// <summary>printed (a printed value exists), computed (only a computed one does) or none.</summary>
    public required string Source { get; init; }
}
