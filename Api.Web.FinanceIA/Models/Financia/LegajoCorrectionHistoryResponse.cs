// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Text.Json.Nodes;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>One correction of a field.</summary>
public sealed record LegajoCorrectionHistoryResponse
{
    /// <summary>The value set.</summary>
    public JsonNode? Value { get; init; }

    /// <summary>The analyst's reason.</summary>
    public required string Reason { get; init; }

    /// <summary>The analyst (a user of this tenant), or null.</summary>
    public string? CorrectedByName { get; init; }

    /// <summary>UTC.</summary>
    public required DateTime CorrectedAt { get; init; }
}
