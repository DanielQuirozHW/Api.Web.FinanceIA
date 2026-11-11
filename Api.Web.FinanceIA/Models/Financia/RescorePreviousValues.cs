// Project : Api.Web.FinanceIA
// Created : 2026-10-22

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The shape of [AnalysisRescores].[PreviousValuesJson]: every AnalysisResults column a re-score
/// overwrites, exactly as it was stored before. The JSON blobs are kept as the raw STRINGS they were —
/// not re-parsed — so copying one back restores the column byte for byte.
/// </summary>
/// <remarks>
/// A balance correction (Phase 10-F3a) also overwrites BalanceJson: its row carries <see cref="Trigger"/>
/// = <see cref="BalanceCorrectionTrigger"/> and the previous <see cref="BalanceJson"/>. Both are omitted
/// when null, so a plain re-score's row is byte-identical to what it was before they existed.
/// </remarks>
public sealed record RescorePreviousValues
{
    /// <summary>The <see cref="Trigger"/> of a re-score run by POST {id}/balance-corrections.</summary>
    public const string BalanceCorrectionTrigger = "balance_correction";

    /// <summary>The JSON path of <see cref="Trigger"/> in PreviousValuesJson, for a server-side JSON_VALUE.</summary>
    public const string TriggerPath = "$.trigger";

    public string?   RatiosJson         { get; init; }
    public string?   BcraDataJson       { get; init; }
    public string?   MacroDataJson      { get; init; }
    public decimal?  AiSuggestedScore   { get; init; }
    public string?   AiSuggestedLevel   { get; init; }
    public string?   ScoreBreakdownJson { get; init; }
    public string?   ScoringNarrative   { get; init; }
    public string?   LufeDataJson       { get; init; }
    public string?   FiscalDataJson     { get; init; }
    public string?   NosisDataJson      { get; init; }
    public string?   CasfogDataJson     { get; init; }
    public string    EnrichmentStatus   { get; init; } = string.Empty;
    public string?   EnrichmentNotes    { get; init; }
    public DateTime? RescoredAt         { get; init; }
    public Guid?     RescoredBy         { get; init; }

    /// <summary>What ran the re-score; null for a plain re-score (POST {id}/rescore).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?   Trigger            { get; init; }

    /// <summary>The BalanceJson before a balance correction, as stored; null when the balance was not changed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?   BalanceJson        { get; init; }

    /// <summary>The ValidationStatus before a balance correction, as stored; null when the balance was not changed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?   ValidationStatus   { get; init; }

    /// <summary>The previous values held by <paramref name="result"/>.</summary>
    public static RescorePreviousValues From(AnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new RescorePreviousValues
        {
            RatiosJson         = result.RatiosJson,
            BcraDataJson       = result.BcraDataJson,
            MacroDataJson      = result.MacroDataJson,
            AiSuggestedScore   = result.AiSuggestedScore,
            AiSuggestedLevel   = result.AiSuggestedLevel,
            ScoreBreakdownJson = result.ScoreBreakdownJson,
            ScoringNarrative   = result.ScoringNarrative,
            LufeDataJson       = result.LufeDataJson,
            FiscalDataJson     = result.FiscalDataJson,
            NosisDataJson      = result.NosisDataJson,
            CasfogDataJson     = result.CasfogDataJson,
            EnrichmentStatus   = result.EnrichmentStatus,
            EnrichmentNotes    = result.EnrichmentNotes,
            RescoredAt         = result.RescoredAt,
            RescoredBy         = result.RescoredBy
        };
    }
}
