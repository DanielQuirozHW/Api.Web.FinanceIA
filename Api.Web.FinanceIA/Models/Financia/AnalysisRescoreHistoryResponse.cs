// Project : Api.Web.FinanceIA
// Created : 2026-10-22

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One item of GET /api/analyses/{id}/rescores. <see cref="RescoredBy"/> is a UserId string, like
/// every other actor id on the analysis payloads; <see cref="RescoredByName"/> is "Usuario" when the
/// id no longer resolves. A persona humana item also carries the suggestion before and after; both are
/// OMITTED on a balance item, which keeps its exact shape.
/// </summary>
public sealed record AnalysisRescoreHistoryResponse(
    Guid     RescoreId,
    DateTime RescoredAt,
    string   RescoredBy,
    string?  RescoredByName,
    decimal? PreviousScore,
    decimal? NewScore,
    string?  PreviousLevel,
    string?  NewLevel,
    string   PreviousEnrichmentStatus,
    string   NewEnrichmentStatus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PreviousSuggestion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NewSuggestion      = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Trigger            = null);
