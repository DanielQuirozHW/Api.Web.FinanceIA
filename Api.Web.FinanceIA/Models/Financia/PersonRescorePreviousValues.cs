// Project : Api.Web.FinanceIA
// Created : 2026-10-30

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The shape of [AnalysisRescores].[PreviousValuesJson] for a persona humana re-evaluation: every column
/// it overwrote — on the AnalysisResults row and on every AnalysisParticipants row — exactly as it was
/// stored before. The JSON blobs are kept as the raw STRINGS they were, not re-parsed, so copying one
/// back restores the column byte for byte and the previous evaluation can be rebuilt from it.
/// </summary>
/// <remarks>
/// <see cref="Suggestions"/> is the one derived node: the solicitante's suggestion before and after, a
/// small index the history list reads with JSON_VALUE (<see cref="PreviousSuggestionPath"/>,
/// <see cref="NewSuggestionPath"/>) instead of parsing <see cref="Participants"/>.
/// </remarks>
public sealed record PersonRescorePreviousValues
{
    /// <summary>JSON_VALUE path of the suggestion before the re-evaluation (camelCase, as stored).</summary>
    public const string PreviousSuggestionPath = "$.suggestions.previous";

    /// <summary>JSON_VALUE path of the suggestion after it.</summary>
    public const string NewSuggestionPath = "$.suggestions.new";

    /// <summary>Always <see cref="AnalysisKinds.PersonaHumana"/>: tells this shape from a balance one.</summary>
    public string Kind { get; init; } = AnalysisKinds.PersonaHumana;

    public required ResultValues               Result       { get; init; }
    public required IReadOnlyList<ParticipantValues> Participants { get; init; }
    public required SuggestionValues           Suggestions  { get; init; }

    /// <summary>The overwritten AnalysisResults columns of a persona analysis.</summary>
    public sealed record ResultValues
    {
        public string?   BcraDataJson     { get; init; }
        public string?   MacroDataJson    { get; init; }
        public string?   FiscalDataJson   { get; init; }
        public string?   LufeDataJson     { get; init; }
        public string    AiInputHash      { get; init; } = string.Empty;
        public string    AiOutputHash     { get; init; } = string.Empty;
        public string    EnrichmentStatus { get; init; } = string.Empty;
        public string?   EnrichmentNotes  { get; init; }
        public DateTime? RescoredAt       { get; init; }
        public Guid?     RescoredBy       { get; init; }

        /// <summary>The previous values held by <paramref name="result"/>.</summary>
        public static ResultValues From(AnalysisResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            return new ResultValues
            {
                BcraDataJson     = result.BcraDataJson,
                MacroDataJson    = result.MacroDataJson,
                FiscalDataJson   = result.FiscalDataJson,
                LufeDataJson     = result.LufeDataJson,
                AiInputHash      = result.AiInputHash,
                AiOutputHash     = result.AiOutputHash,
                EnrichmentStatus = result.EnrichmentStatus,
                EnrichmentNotes  = result.EnrichmentNotes,
                RescoredAt       = result.RescoredAt,
                RescoredBy       = result.RescoredBy
            };
        }
    }

    /// <summary>
    /// The overwritten AnalysisParticipants columns of one row, with the identity that locates it (never
    /// overwritten itself).
    /// </summary>
    public sealed record ParticipantValues
    {
        public required Guid    ParticipantId        { get; init; }
        public required string  Role                 { get; init; }
        public Guid?            FiadorId             { get; init; }
        public required Guid    PersonLegajoId       { get; init; }
        public required string  DocumentVersionsJson { get; init; }
        public required string  FiltersJson          { get; init; }
        public required string  IndicatorsJson       { get; init; }
        public required string  CoverageJson         { get; init; }
        public required string  EvaluationVersion    { get; init; }
        public required DateTime CreatedAt           { get; init; }

        /// <summary>The previous values held by <paramref name="participant"/>.</summary>
        public static ParticipantValues From(AnalysisParticipant participant)
        {
            ArgumentNullException.ThrowIfNull(participant);

            return new ParticipantValues
            {
                ParticipantId        = participant.ParticipantId,
                Role                 = participant.Role,
                FiadorId             = participant.FiadorId,
                PersonLegajoId       = participant.PersonLegajoId,
                DocumentVersionsJson = participant.DocumentVersionsJson,
                FiltersJson          = participant.FiltersJson,
                IndicatorsJson       = participant.IndicatorsJson,
                CoverageJson         = participant.CoverageJson,
                EvaluationVersion    = participant.EvaluationVersion,
                CreatedAt            = participant.CreatedAt
            };
        }
    }

    /// <summary>The solicitante's suggestion before and after (<see cref="PersonSuggestions"/>).</summary>
    public sealed record SuggestionValues(string Previous, string New);
}
