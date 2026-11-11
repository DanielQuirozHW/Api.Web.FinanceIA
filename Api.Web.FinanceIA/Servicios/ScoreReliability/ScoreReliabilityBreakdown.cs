// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Servicios.ScoreReliability;

/// <summary>
/// The pieces of <c>AnalysisResults.ScoreBreakdownJson</c> the reliability verdict reads — exactly these
/// top-level keys and nothing else. The detail and the dashboard deserialise the blob into this type
/// (through <see cref="ScoreReliabilityAdapter.Parse"/>); the list deserialises the same keys into its own
/// narrow shape and hands them over through the same constructor, so all three read the same values.
/// </summary>
internal sealed class ScoreReliabilityBreakdown
{
    /// <summary>The per-ratio entries — only what the verdict needs of each.</summary>
    [JsonPropertyName("breakdown")]
    public Dictionary<string, ScoreReliabilityRatioEntry>? Breakdown { get; set; }

    [JsonPropertyName("ratiosLowCoverageFallback")]
    public bool? RatiosLowCoverageFallback { get; set; }

    [JsonPropertyName("nonComputableRatios")]
    public List<string>? NonComputableRatios { get; set; }

    [JsonPropertyName("bcraExcluded")]
    public bool? BcraExcluded { get; set; }

    [JsonPropertyName("bcraExclusionReason")]
    public string? BcraExclusionReason { get; set; }

    /// <summary>Ratio keys whose stored thresholds were out of order and fell back to the defaults.</summary>
    [JsonPropertyName("thresholdFallbacks")]
    public List<string>? ThresholdFallbacks { get; set; }

    /// <summary>The ratios whose partial score reached the critical-floor trigger (score credibility 1b-5).</summary>
    [JsonPropertyName("criticalRatios")]
    public List<string>? CriticalRatios { get; set; }

    /// <summary>The floor computed from <see cref="CriticalRatios"/>, whether or not it bound (see <c>CriticalFloor</c>).</summary>
    [JsonPropertyName("appliedFloor")]
    public decimal? AppliedFloor { get; set; }

    /// <summary>The composite before the floor; absent on blobs that predate it.</summary>
    [JsonPropertyName("scoreBeforeFloor")]
    public decimal? ScoreBeforeFloor { get; set; }

    /// <summary>
    /// The #300 facts block, kept as a raw element so ABSENT (<see cref="JsonValueKind.Undefined"/>, a row
    /// scored before #300) and JSON NULL (the builder failed for this row) stay distinguishable.
    /// </summary>
    [JsonPropertyName("reliabilityFacts")]
    public JsonElement ReliabilityFacts { get; set; }
}

/// <summary>What the verdict reads of one <c>breakdown</c> entry (see <c>RatioBreakdownEntry</c>).</summary>
internal sealed class ScoreReliabilityRatioEntry
{
    [JsonPropertyName("excluded")]
    public bool Excluded { get; set; }

    [JsonPropertyName("exclusionReason")]
    public string? ExclusionReason { get; set; }

    [JsonPropertyName("measured")]
    public bool? Measured { get; set; }
}

/// <summary>How the breakdown string read.</summary>
internal enum ScoreReliabilityBreakdownState
{
    /// <summary>No breakdown at all (null, blank or the JSON literal null).</summary>
    Absent,

    /// <summary>Read.</summary>
    Parsed,

    /// <summary>Present but unreadable — logged by the surface, never thrown.</summary>
    Malformed,
}
