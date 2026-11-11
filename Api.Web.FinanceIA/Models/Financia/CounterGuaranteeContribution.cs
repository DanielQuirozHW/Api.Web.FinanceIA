// Project : Api.Web.FinanceIA
// Created : 2026-10-29

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>What one fiador adds to the aggregate counter-guarantee coverage (M10 Phase 5a-2).</summary>
public sealed record CounterGuaranteeContribution
{
    /// <summary>The Fiadores row.</summary>
    public required Guid FiadorId { get; init; }

    /// <summary>The AnalysisParticipants row.</summary>
    public required Guid ParticipantId { get; init; }

    /// <summary>False when the headline coverage leaves the fiador out (a suggested rejection).</summary>
    public required bool Counted { get; init; }

    /// <summary><see cref="FiadorAssetReasons.RechazoSugerido"/> when not counted; null otherwise.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// min(max(0, executable assets after de-duplication − liabilities − guarantees given), amount at charge),
    /// in the headline computation; 0 when not counted.
    /// </summary>
    public required decimal Contribution { get; init; }

    /// <summary>The same, in the computation that also counts the fiadores with a suggested rejection.</summary>
    public required decimal ContributionIncludingFlagged { get; init; }

    /// <summary>Items of this fiador not counted because another fiador already counts the same asset (headline computation).</summary>
    public IReadOnlyList<Guid> DuplicateItemIds { get; init; } = [];
}
