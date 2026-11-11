// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The outcome of LegajoIntegrityService over one extraction (M10 Phase 2b) — the shape of
/// LegajoExtractions.ChecksJson. A FLAG for the analyst: it never corrects a figure.
/// </summary>
public sealed record LegajoIntegrityReport
{
    /// <summary>True when no evaluated check failed. Skipped checks do not count.</summary>
    [JsonPropertyName("passed")]
    public required bool Passed { get; init; }

    /// <summary>The printed unit the figures were scaled from: Pesos, Miles or Millones.</summary>
    [JsonPropertyName("amountsUnit")]
    public required string AmountsUnit { get; init; }

    /// <summary>True when the document's unit was not read and Pesos was assumed.</summary>
    [JsonPropertyName("amountsUnitAssumed")]
    public bool AmountsUnitAssumed { get; init; }

    [JsonPropertyName("checks")]
    public required IReadOnlyList<LegajoIntegrityCheck> Checks { get; init; }

    /// <summary>Checks that did not run because an operand was not extracted.</summary>
    [JsonPropertyName("skipped")]
    public required IReadOnlyList<LegajoIntegritySkip> Skipped { get; init; }

    /// <summary>The checks that failed.</summary>
    [JsonIgnore]
    public IEnumerable<LegajoIntegrityCheck> Failed => Checks.Where(c => !c.Passed);
}
