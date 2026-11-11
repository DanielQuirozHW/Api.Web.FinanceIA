// Project : Api.Web.FinanceIA
// Created : 2026-11-06

using System.Text.Json;
using System.Text.Json.Serialization;
using Api.Web.FinanceIA.Servicios.ScoreReliability;

namespace Api.Web.FinanceIA.Servicios.AnalysisComparison;

/// <summary>
/// The keys of <c>AnalysisResults.ScoreBreakdownJson</c> the previous-analysis comparison reads, and
/// nothing else. The same key names the detail's other readers use (<c>AnalysisController.ChatScoreBreakdown</c>,
/// <c>PdfExportService.ScoreBreakdownWrapper</c>); those are private, and the list's
/// <c>ListScoreBreakdown</c> has its key set pinned by a test, so this narrow copy is its own.
/// All scores on the internal 0–100 scale (0 = healthy).
/// </summary>
internal sealed class ComparisonScoreBreakdown
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    [JsonPropertyName("scoreRatios")]
    public decimal? ScoreRatios { get; set; }

    [JsonPropertyName("scoreBcra")]
    public decimal? ScoreBcra { get; set; }

    [JsonPropertyName("scoreMacro")]
    public decimal? ScoreMacro { get; set; }

    /// <summary>True when BCRA was excluded from the score; its scoreBcra is then a placeholder.</summary>
    [JsonPropertyName("bcraExcluded")]
    public bool? BcraExcluded { get; set; }

    /// <summary>The declared-sales variation the score USED (§7.3), as a fraction: −0.25 = −25 %.</summary>
    [JsonPropertyName("ventasVariacion")]
    public decimal? VentasVariacion { get; set; }

    /// <summary>
    /// <c>ScoringService.ScoringAlgoVersion</c> at scoring time. Kept as a raw element so a string or a
    /// number both read; absent on rows scored before it existed, which are version 1.
    /// </summary>
    [JsonPropertyName("scoringAlgoVersion")]
    public JsonElement ScoringAlgoVersionRaw { get; set; }

    /// <summary>
    /// The scoring algorithm version, "1" when the row predates the field. Ignored by the serializer: its
    /// name would otherwise collide, case-insensitively, with <see cref="ScoringAlgoVersionRaw"/>'s and make
    /// every breakdown unreadable.
    /// </summary>
    [JsonIgnore]
    public string ScoringAlgoVersion => ScoringAlgoVersionRaw.ValueKind switch
    {
        JsonValueKind.String => ScoringAlgoVersionRaw.GetString() is { Length: > 0 } version ? version : "1",
        JsonValueKind.Number => ScoringAlgoVersionRaw.GetRawText(),
        _                    => "1",
    };

    /// <summary>
    /// Parses a breakdown. Never throws: blank or the JSON literal null is Absent, a blob that does not
    /// read is Malformed — the contract of <c>AnalysisController.TryDeserializeListBreakdown</c>.
    /// </summary>
    public static (ComparisonScoreBreakdown? Breakdown, ScoreReliabilityBreakdownState State) Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (null, ScoreReliabilityBreakdownState.Absent);

        try
        {
            var breakdown = JsonSerializer.Deserialize<ComparisonScoreBreakdown>(json, JsonOpts);
            return breakdown is null
                ? (null, ScoreReliabilityBreakdownState.Absent)
                : (breakdown, ScoreReliabilityBreakdownState.Parsed);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return (null, ScoreReliabilityBreakdownState.Malformed);
        }
    }
}
