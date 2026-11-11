// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;

namespace Api.Web.FinanceIA.Servicios.ScoreReliability;

/// <summary>A verdict, and whether the breakdown behind it could not be read (for the surface to log).</summary>
/// <param name="Reliability">The verdict; null on a persona humana analysis.</param>
/// <param name="BreakdownUnreadable">True when the breakdown or its facts block was present and unreadable.</param>
internal readonly record struct ScoreReliabilityVerdict(ScoreReliabilityResponse? Reliability, bool BreakdownUnreadable);

/// <summary>
/// The ONE adapter from (row scalars + <c>ScoreBreakdownJson</c>) to a score-reliability verdict (score
/// credibility 1b-2b). The analysis detail, every decision / trigger / re-score response, the list and the
/// dashboard all go through it, with the same inputs and the same JSON options, so the same row gets the
/// same verdict on every surface.
/// <para>Never throws: an unreadable breakdown becomes <c>facts_unavailable</c> and is flagged for the
/// caller to log (once per response — never per list row).</para>
/// </summary>
internal static class ScoreReliabilityAdapter
{
    /// <summary>Parses (row scalars + breakdown string) and evaluates. The detail and the dashboard use this.</summary>
    public static ScoreReliabilityVerdict Evaluate(ScoreReliabilityRow row, string? scoreBreakdownJson)
    {
        ArgumentNullException.ThrowIfNull(row);

        var (breakdown, state) = Parse(scoreBreakdownJson);
        return Evaluate(row, breakdown, state);
    }

    /// <summary>Evaluates an already-parsed breakdown. The list uses this, from its own narrow parse.</summary>
    public static ScoreReliabilityVerdict Evaluate(
        ScoreReliabilityRow row, ScoreReliabilityBreakdown? breakdown, ScoreReliabilityBreakdownState state)
    {
        ArgumentNullException.ThrowIfNull(row);

        var input = BuildInput(row, breakdown, state);
        return new ScoreReliabilityVerdict(ScoreReliabilityEvaluator.Evaluate(input), input.BreakdownUnreadable);
    }

    /// <summary>
    /// Reads the keys the verdict needs. Never throws: blank or the JSON literal null is
    /// <see cref="ScoreReliabilityBreakdownState.Absent"/>, anything that does not read as an object of the
    /// expected shape is <see cref="ScoreReliabilityBreakdownState.Malformed"/>.
    /// </summary>
    public static (ScoreReliabilityBreakdown? Breakdown, ScoreReliabilityBreakdownState State) Parse(string? scoreBreakdownJson)
    {
        if (string.IsNullOrWhiteSpace(scoreBreakdownJson))
            return (null, ScoreReliabilityBreakdownState.Absent);

        try
        {
            // The list's options (AnalysisController reads ListScoreBreakdown with these), so the two parses
            // of the same blob cannot disagree on a key's casing.
            var breakdown = JsonSerializer.Deserialize<ScoreReliabilityBreakdown>(
                scoreBreakdownJson, AnalysisPayloadJson.BcraJsonOpts);

            return breakdown is null
                ? (null, ScoreReliabilityBreakdownState.Absent)
                : (breakdown, ScoreReliabilityBreakdownState.Parsed);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return (null, ScoreReliabilityBreakdownState.Malformed);
        }
    }

    /// <summary>Normalises the parsed pieces into the evaluator's input. Internal for tests.</summary>
    internal static ScoreReliabilityInput BuildInput(
        ScoreReliabilityRow row, ScoreReliabilityBreakdown? breakdown, ScoreReliabilityBreakdownState state)
    {
        if (state == ScoreReliabilityBreakdownState.Malformed)
        {
            return new ScoreReliabilityInput
            {
                Row                 = row,
                FactsState          = ScoreReliabilityFactsState.Unreadable,
                BreakdownUnreadable = true,
            };
        }

        if (breakdown is null)
            return new ScoreReliabilityInput { Row = row, FactsState = ScoreReliabilityFactsState.Absent };

        var (factsState, facts) = ReadFacts(breakdown.ReliabilityFacts);
        var criticalRatios      = Clean(breakdown.CriticalRatios);

        return new ScoreReliabilityInput
        {
            Row                       = row,
            FactsState                = factsState,
            Facts                     = facts,
            BreakdownUnreadable       = factsState == ScoreReliabilityFactsState.Unreadable,
            RatiosLowCoverageFallback = breakdown.RatiosLowCoverageFallback == true,
            Ratios                    = breakdown.Breakdown is { } ratios
                ? ratios.Where(kv => kv.Value is not null)
                        .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)
                : new Dictionary<string, ScoreReliabilityRatioEntry>(StringComparer.Ordinal),
            NonComputableRatios       = Clean(breakdown.NonComputableRatios),
            BcraExcluded              = breakdown.BcraExcluded == true,
            BcraExclusionReason       = breakdown.BcraExclusionReason,
            ThresholdFallbacks        = Clean(breakdown.ThresholdFallbacks),
            CriticalRatios            = criticalRatios,
            CriticalFloorApplied      = criticalRatios.Count > 0
                                        && CriticalFloor.WasApplied(breakdown.ScoreBeforeFloor, breakdown.AppliedFloor),
            ScoreBeforeFloor          = breakdown.ScoreBeforeFloor,
        };
    }

    private static (ScoreReliabilityFactsState, ScoreReliabilityFacts?) ReadFacts(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Undefined:
                return (ScoreReliabilityFactsState.Absent, null);
            case JsonValueKind.Null:
                return (ScoreReliabilityFactsState.NotRecorded, null);
            case JsonValueKind.Object:
                try
                {
                    var facts = element.Deserialize<ScoreReliabilityFacts>(AnalysisPayloadJson.BcraJsonOpts);
                    return facts is null
                        ? (ScoreReliabilityFactsState.Unreadable, null)
                        : (ScoreReliabilityFactsState.Present, facts);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
                {
                    return (ScoreReliabilityFactsState.Unreadable, null);
                }
            default:
                return (ScoreReliabilityFactsState.Unreadable, null);
        }
    }

    private static IReadOnlyList<string> Clean(List<string>? keys)
        => keys is null ? [] : keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal).ToList();
}
