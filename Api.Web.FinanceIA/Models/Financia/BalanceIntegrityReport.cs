// Project : Api.Web.FinanceIA
// Created : 2026-09-24

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Outcome of the deterministic integrity checks over one extraction, persisted inside BalanceJson
/// under <c>integrity</c> (null when the evaluation itself failed; absent on analyses predating
/// prompt 2.18). A FLAG for the analyst — it never corrects a figure and never feeds the score.
/// </summary>
public sealed record BalanceIntegrityReport
{
    /// <summary>True when no evaluated check failed. Checks that could not run do not count.</summary>
    [JsonPropertyName("passed")]
    public required bool Passed { get; init; }

    [JsonPropertyName("checks")]
    public required IReadOnlyList<BalanceIntegrityCheck> Checks { get; init; }

    /// <summary>Checks that did not run because an operand was not extracted.</summary>
    [JsonPropertyName("skipped")]
    public required IReadOnlyList<string> Skipped { get; init; }

    /// <summary>
    /// Scoring ratio keys (<see cref="ScoringDefaults.ThresholdRatios"/> order) computed from a line
    /// involved in a failed check — the ratios the analyst should not take at face value.
    /// </summary>
    [JsonPropertyName("affectedRatios")]
    public required IReadOnlyList<string> AffectedRatios { get; init; }

    /// <summary>Printed unit the figures were scaled from: pesos, miles or millones.</summary>
    [JsonPropertyName("amountsUnit")]
    public string AmountsUnit { get; init; } = BalanceAmountsUnit.Pesos;

    /// <summary>True when the document's unit was not reported and pesos was assumed.</summary>
    [JsonPropertyName("amountsUnitAssumed")]
    public bool AmountsUnitAssumed { get; init; }

    /// <summary>True when a failed check capped the extraction confidence.</summary>
    [JsonPropertyName("confidenceCapped")]
    public bool ConfidenceCapped { get; init; }

    /// <summary>Confidence the model reported for this extraction.</summary>
    [JsonPropertyName("reportedConfidence")]
    public double? ReportedConfidence { get; init; }

    /// <summary>Confidence after the cap (equal to the reported one when not capped).</summary>
    [JsonPropertyName("effectiveConfidence")]
    public double? EffectiveConfidence { get; init; }

    /// <summary>
    /// How the kept extraction was obtained: whether a second, differently-read pass ran, why, and
    /// which pass was kept. Written by ClaudeService on every extraction since this field shipped
    /// (also when no second pass ran); absent on analyses persisted before it, which a reader must
    /// treat as "unknown", not as "no second pass".
    /// </summary>
    [JsonPropertyName("extraction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExtractionPassInfo? Extraction { get; init; }

    /// <summary>
    /// True when this report was re-evaluated over figures an analyst corrected by hand
    /// (POST /api/analyses/{resultId}/balance-corrections). Omitted (null) on every extraction, so an
    /// extraction's BalanceJson is byte-identical to what it was before this field existed.
    /// </summary>
    [JsonPropertyName("manuallyCorrected")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ManuallyCorrected { get; init; }
}
