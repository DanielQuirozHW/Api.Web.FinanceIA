// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Text.Json.Nodes;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>One field of the effective view.</summary>
public sealed record LegajoExtractionFieldResponse
{
    /// <summary>JSON Pointer — the path a correction names.</summary>
    public required string FieldPath { get; init; }

    /// <summary>The effective value (JSON: number, string, boolean, object or null), in the printed unit.</summary>
    public JsonNode? Value { get; init; }

    /// <summary>1-based page the model read it on (a row's page for a row value).</summary>
    public int? Page { get; init; }

    /// <summary>The literal text it was read from (the row, for a row value).</summary>
    public string? Quote { get; init; }

    /// <summary>The model's confidence, 0..1.</summary>
    public decimal? Confidence { get; init; }

    /// <summary>How the run read the document.</summary>
    public required string ReadMode { get; init; }

    /// <summary>True when an analyst corrected it (or added its row).</summary>
    public required bool Corrected { get; init; }

    /// <summary>The corrections that set it, oldest first.</summary>
    public required IReadOnlyList<LegajoCorrectionHistoryResponse> History { get; init; }

    /// <summary>
    /// True when POST .../extraction/corrections accepts this path (given a valid value): the field's
    /// own node exists in the effective values. False under a null section (e.g. a manifestación with no
    /// incomeStatement) and for a row's null match-key, which is a whole object, not a leaf.
    /// </summary>
    public required bool Correctable { get; init; }
}
