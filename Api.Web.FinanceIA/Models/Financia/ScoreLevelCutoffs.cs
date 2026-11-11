// Project : Api.Web.FinanceIA
// Created : 2026-11-04

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The three motor-scale cutoffs (0–100, 0 = no risk) that turn a score into its risk level:
/// <c>score &lt;= Low</c> → Low, <c>&lt;= Medium</c> → Medium, <c>&lt;= High</c> → High, above → Critical.
/// <para>
/// THE single resolution rule for <c>score_threshold_low/medium/high</c>: the engine classifies with
/// <see cref="Resolve"/>, records the result in <c>ScoreBreakdownJson.levelCutoffs</c>, and
/// GET /api/billing/usage reports the tenant's current base values through the same method. No other
/// place may read those keys or hardcode 35 / 60 / 80 — that is how a Portal label ends up
/// contradicting the level the BFF stored.
/// </para>
/// <para>
/// Reporting only: recording the cutoffs moves no score and no level, so it is not a scoring
/// algorithm change. Rows scored before it existed carry no <c>levelCutoffs</c> key at all.
/// </para>
/// </summary>
/// <param name="Low">Highest score classified as Low.</param>
/// <param name="Medium">Highest score classified as Medium.</param>
/// <param name="High">Highest score classified as High; anything above is Critical.</param>
public sealed record ScoreLevelCutoffs(
    [property: JsonPropertyName("low")]    decimal Low,
    [property: JsonPropertyName("medium")] decimal Medium,
    [property: JsonPropertyName("high")]   decimal High)
{
    /// <summary>The config key of <see cref="Low"/>.</summary>
    public const string LowKey = "score_threshold_low";

    /// <summary>The config key of <see cref="Medium"/>.</summary>
    public const string MediumKey = "score_threshold_medium";

    /// <summary>The config key of <see cref="High"/>.</summary>
    public const string HighKey = "score_threshold_high";

    /// <summary>
    /// Reads the three cutoffs from a merged tenant config map, falling back per key to
    /// <see cref="ScoringDefaults"/> exactly as every other engine parameter does. Values are returned
    /// as stored (no rounding): they are the numbers the classification compares against.
    /// </summary>
    public static ScoreLevelCutoffs Resolve(IReadOnlyDictionary<string, decimal> cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);

        return new ScoreLevelCutoffs(
            cfg.TryGetValue(LowKey,    out var low)    ? low    : ScoringDefaults.Get(LowKey),
            cfg.TryGetValue(MediumKey, out var medium) ? medium : ScoringDefaults.Get(MediumKey),
            cfg.TryGetValue(HighKey,   out var high)   ? high   : ScoringDefaults.Get(HighKey));
    }

    /// <summary>The level a score falls in under these cutoffs: Low / Medium / High / Critical.</summary>
    public string Classify(decimal score)
        => score <= Low    ? "Low"
         : score <= Medium ? "Medium"
         : score <= High   ? "High"
         :                   "Critical";
}
