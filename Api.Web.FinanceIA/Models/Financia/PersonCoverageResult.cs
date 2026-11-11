// Project : Api.Web.FinanceIA
// Created : 2026-10-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The documentary coverage index, as frozen in AnalysisParticipants.CoverageJson (M10 Phase 4a): what the
/// regime expects, what is there and how recent, and what cannot be concluded. Separate from any score, and
/// it blocks nothing by itself.
/// </summary>
public sealed record PersonCoverageResult
{
    /// <summary>The regime the matrix was chosen by.</summary>
    public required string FiscalRegime { get; init; }

    /// <summary>One entry per expected document.</summary>
    public required IReadOnlyList<PersonCoverageItem> Items { get; init; }

    /// <summary>Required items with a Ready document.</summary>
    public required int RequiredPresent { get; init; }

    /// <summary>Required items in the matrix.</summary>
    public required int RequiredTotal { get; init; }

    /// <summary><see cref="RequiredPresent"/> / <see cref="RequiredTotal"/>, 4 decimals. A ratio, not a score.</summary>
    public required decimal Index { get; init; }

    /// <summary>The age of the BCRA and ARCA snapshots.</summary>
    public required IReadOnlyList<PersonCoverageSnapshot> Snapshots { get; init; }

    /// <summary>Explicit "cannot conclude" statements.</summary>
    public required IReadOnlyList<PersonCannotConclude> CannotConclude { get; init; }
}
