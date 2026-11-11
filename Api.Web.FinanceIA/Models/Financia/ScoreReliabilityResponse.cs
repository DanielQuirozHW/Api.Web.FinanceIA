// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// "Confiabilidad del score" (score credibility 1b-2b): how far a score can be trusted, derived at READ
/// time from the facts the score was built on (<c>ScoreBreakdownJson.reliabilityFacts</c>, #300) and a few
/// row scalars, measured against the score's own date. Computed in exactly one place,
/// <c>Servicios.ScoreReliability.ScoreReliabilityAdapter</c>, and carried as <c>scoreReliability</c> by the
/// analysis detail (and every decision / trigger / re-score response), the list and the dashboard items
/// that show a score — so no two surfaces can disagree about the same row.
/// <para>
/// Reporting only: it moves no score and no level. Null on a persona humana analysis, which is scored by
/// another engine.
/// </para>
/// </summary>
public sealed record ScoreReliabilityResponse
{
    /// <summary><c>complete</c> | <c>with_caveats</c> | <c>provisional</c> | <c>none</c>; see <see cref="ScoreReliabilityLevels"/>.</summary>
    [JsonPropertyName("level")]
    public required string Level { get; init; }

    /// <summary>Why the level is not <c>complete</c>, ordered none → provisional → caveat. Empty when complete.</summary>
    [JsonPropertyName("reasons")]
    public required IReadOnlyList<ScoreReliabilityReasonResponse> Reasons { get; init; }

    /// <summary>The version of the rules and thresholds that produced this verdict.</summary>
    [JsonPropertyName("policyVersion")]
    public required string PolicyVersion { get; init; }
}

/// <summary>One reason behind a <see cref="ScoreReliabilityResponse"/>: a stable code, its weight and an es-AR sentence.</summary>
public sealed record ScoreReliabilityReasonResponse
{
    /// <summary>A stable snake_case code the Portal can key on (never shown to the analyst).</summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary><c>none</c> | <c>provisional</c> | <c>caveat</c>; see <see cref="ScoreReliabilitySeverities"/>.</summary>
    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    /// <summary>The es-AR explanation shown to the analyst.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>
    /// The numbers, dates and ratio keys the evaluator put into <see cref="Message"/>, recorded beside it so
    /// a Claude prompt can state them through a FIXED phrase without ever reading the message (score
    /// credibility 1b-3: a message can quote free text, e.g. the sector). Never serialised — non-public and
    /// ignored — so the wire shape is unchanged. Null when the reason carries no variable part.
    /// </summary>
    [JsonIgnore]
    internal ScoreReliabilityReasonArgs? Args { get; init; }
}

/// <summary>
/// The prompt-safe variable parts of one reason (see <see cref="ScoreReliabilityReasonResponse.Args"/>).
/// Numbers, UTC instants, YYYY-MM periods and ratio KEYS only: deliberately no free-text field, so nothing
/// identifying can ride it into a prompt.
/// </summary>
internal sealed record ScoreReliabilityReasonArgs
{
    /// <summary>Ratio keys, in the order the message names them.</summary>
    public IReadOnlyList<string> RatioKeys { get; init; } = [];

    /// <summary>A count (failed integrity checks).</summary>
    public int? Count { get; init; }

    /// <summary>A fiscal year.</summary>
    public int? FiscalYear { get; init; }

    /// <summary>A whole number of months (the age of <see cref="DateUtc"/>, or of the balance).</summary>
    public int? Months { get; init; }

    /// <summary>A UTC instant (shown as the Argentine calendar day).</summary>
    public DateTime? DateUtc { get; init; }

    /// <summary>A YYYY-MM period.</summary>
    public (int Year, int Month)? Periodo { get; init; }

    /// <summary>The age in months of <see cref="Periodo"/>.</summary>
    public int? PeriodoMonths { get; init; }

    /// <summary>A score on the displayed 0–1000 scale (<c>ScoreScale.ToNosis</c>).</summary>
    public int? Score { get; init; }
}

/// <summary>The closed vocabulary for <see cref="ScoreReliabilityResponse.Level"/>.</summary>
public static class ScoreReliabilityLevels
{
    /// <summary>Every fact checked out.</summary>
    public const string Complete = "complete";

    /// <summary>The score stands, with caveats the analyst should read.</summary>
    public const string WithCaveats = "with_caveats";

    /// <summary>A fact the score rests on is missing or doubtful: treat the score as provisional.</summary>
    public const string Provisional = "provisional";

    /// <summary>There is no score to judge.</summary>
    public const string None = "none";
}

/// <summary>The closed vocabulary for <see cref="ScoreReliabilityReasonResponse.Severity"/>.</summary>
public static class ScoreReliabilitySeverities
{
    /// <summary>No score at all.</summary>
    public const string None = "none";

    /// <summary>Makes the score provisional.</summary>
    public const string Provisional = "provisional";

    /// <summary>A caveat; the score stands.</summary>
    public const string Caveat = "caveat";
}
