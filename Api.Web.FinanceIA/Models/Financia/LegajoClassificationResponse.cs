// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>The current classification of a legajo document — the reading, never the holder CUIT.</summary>
public sealed record LegajoClassificationResponse
{
    /// <summary>The type the model detected (a legajo type or "Desconocido").</summary>
    public required string DetectedType { get; init; }

    /// <summary>0..1.</summary>
    public decimal? Confidence { get; init; }

    /// <summary>True when the PDF looks like several documents.</summary>
    public required bool IsComposite { get; init; }

    /// <summary>cuit_missing, cuit_invalid, cuit_mismatch, type_mismatch, composite, low_confidence.</summary>
    public required IReadOnlyList<string> ReviewReasons { get; init; }
}
