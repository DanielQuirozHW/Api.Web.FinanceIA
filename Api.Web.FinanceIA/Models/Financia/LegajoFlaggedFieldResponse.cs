// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>One entry of the flagged queue.</summary>
public sealed record LegajoFlaggedFieldResponse
{
    /// <summary>JSON Pointer of the field or row.</summary>
    public required string FieldPath { get; init; }

    /// <summary>low_confidence and/or check_failed.</summary>
    public required IReadOnlyList<string> Reasons { get; init; }
}
