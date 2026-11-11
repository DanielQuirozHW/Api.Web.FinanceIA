// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Text.Json;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>Body of POST /api/documents/{id}/extraction/corrections.</summary>
public sealed record LegajoCorrectionRequest
{
    /// <summary>JSON Pointer of the field, e.g. <c>/totals/totalBienes</c> or <c>/items/3/fiscalValue</c>.</summary>
    public string? FieldPath { get; init; }

    /// <summary>The new value as JSON, in the unit the document prints; <c>null</c> clears the field.</summary>
    public JsonElement Value { get; init; }

    /// <summary>Why — at least five non-blank characters.</summary>
    public string? Reason { get; init; }
}
