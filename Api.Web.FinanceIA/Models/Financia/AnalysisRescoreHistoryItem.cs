// Project : Api.Web.FinanceIA
// Created : 2026-10-22

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One [AnalysisRescores] row as the history endpoint reads it: everything but PreviousValuesJson and
/// SourcesJson, which are the reversal record and are never served in a list. For a persona humana
/// re-evaluation the two suggestions are read server-side out of PreviousValuesJson (JSON_VALUE on its
/// <c>suggestions</c> node), so the blob itself is never selected; a balance row reads null.
/// </summary>
public sealed record AnalysisRescoreHistoryItem(
    Guid     RescoreId,
    DateTime RescoredAt,
    Guid     RescoredBy,
    decimal? PreviousScore,
    decimal? NewScore,
    string?  PreviousLevel,
    string?  NewLevel,
    string   PreviousEnrichmentStatus,
    string   NewEnrichmentStatus,
    string?  PreviousSuggestion = null,
    string?  NewSuggestion      = null,
    string?  Trigger            = null);
