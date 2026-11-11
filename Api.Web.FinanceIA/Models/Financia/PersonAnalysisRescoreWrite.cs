// Project : Api.Web.FinanceIA
// Created : 2026-10-30

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One re-evaluation of a persona humana analysis, ready to persist: the new value of every AnalysisResults
/// column the persona evaluation derives, the new evaluation of every participant row, the concurrency
/// expectation and the history row recording the previous values.
/// </summary>
/// <remarks>
/// These are the ONLY columns a re-evaluation touches. The balance-only columns stay NULL, ValidationStatus
/// and GeneratedAt keep their stored values, and no Analyses column is written.
/// </remarks>
public sealed record PersonAnalysisRescoreWrite
{
    public required Guid     ResultId             { get; init; }
    public required Guid     AnalysisId           { get; init; }
    public required string   TenantId             { get; init; }

    /// <summary>The RescoreCount read before computing; the UPDATE only applies while it still holds.</summary>
    public required int      ExpectedRescoreCount { get; init; }

    public required DateTime RescoredAt           { get; init; }
    public required Guid     RescoredBy           { get; init; }

    public string?           BcraDataJson         { get; init; }
    public string?           MacroDataJson        { get; init; }
    public string?           FiscalDataJson       { get; init; }
    public string?           LufeDataJson         { get; init; }
    public required string   AiInputHash          { get; init; }
    public required string   AiOutputHash         { get; init; }
    public required string   EnrichmentStatus     { get; init; }
    public string?           EnrichmentNotes      { get; init; }

    /// <summary>The solicitante first, then the fiadores; one per stored row of the analysis.</summary>
    public required IReadOnlyList<PersonParticipantReevaluation> Participants { get; init; }

    /// <summary>The append-only history row, inserted in the same transaction as the updates.</summary>
    public required AnalysisRescore History       { get; init; }
}
