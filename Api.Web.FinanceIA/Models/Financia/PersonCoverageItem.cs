// Project : Api.Web.FinanceIA
// Created : 2026-10-20

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>One expected document of the coverage index (M10 Phase 4a).</summary>
public sealed record PersonCoverageItem
{
    /// <summary>The expected type (<see cref="LegajoExpectedDocument.DocumentType"/>).</summary>
    public required string DocumentType { get; init; }

    /// <summary>Required by the regime's matrix.</summary>
    public required bool Required { get; init; }

    /// <summary>Types that also satisfy it.</summary>
    public IReadOnlyList<string> Alternatives { get; init; } = [];

    /// <summary>True when at least one Ready document satisfies it.</summary>
    public required bool Present { get; init; }

    /// <summary>The Ready documents that satisfy it.</summary>
    public IReadOnlyList<Guid> ReadyDocumentIds { get; init; } = [];

    /// <summary>Documents that satisfy it but are still in the pipeline or in review.</summary>
    public IReadOnlyList<Guid> PendingDocumentIds { get; init; } = [];

    /// <summary>The latest PeriodEnd among the Ready documents.</summary>
    public DateOnly? LatestPeriodEnd { get; init; }

    /// <summary>Whole months from <see cref="LatestPeriodEnd"/> to the evaluation date.</summary>
    public int? AgeMonths { get; init; }

    /// <summary>The recency cut applied; null for a type with no recency rule.</summary>
    public int? MaxAgeMonths { get; init; }

    /// <summary>True when the latest Ready document is older than <see cref="MaxAgeMonths"/>.</summary>
    public bool IsStale { get; init; }

    /// <summary>
    /// Agro annual mode (engine 0.7): true when a monthly DDJJ is older than <see cref="MaxAgeMonths"/> but every
    /// month of the gap is off-season (no sales in that calendar month), so it is NOT stale. Null otherwise,
    /// and left out of the frozen JSON, so every evaluation outside agro mode reads byte for byte as in 0.6.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SeasonalGap { get; init; }
}
