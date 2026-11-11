// Project : Api.Web.FinanceIA
// Created : 2026-10-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>The age of one external report on the legajo, for the coverage index (M10 Phase 4a).</summary>
public sealed record PersonCoverageSnapshot
{
    /// <summary>BCRA or ARCA.</summary>
    public required string Source { get; init; }

    /// <summary>When the legajo's snapshot was last refreshed; null when never.</summary>
    public DateTime? UpdatedAt { get; init; }

    /// <summary>Whole days from <see cref="UpdatedAt"/> to the evaluation date.</summary>
    public int? AgeDays { get; init; }

    /// <summary>pf_informe_max_dias.</summary>
    public required int MaxAgeDays { get; init; }

    /// <summary>Missing or older than <see cref="MaxAgeDays"/>.</summary>
    public required bool IsStale { get; init; }
}
