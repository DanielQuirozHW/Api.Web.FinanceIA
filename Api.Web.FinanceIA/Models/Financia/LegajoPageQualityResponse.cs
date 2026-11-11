// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>How many pages of a legajo document got each text-quality verdict.</summary>
public sealed record LegajoPageQualityResponse
{
    /// <summary>Pages with a clean text layer.</summary>
    public required int Good { get; init; }

    /// <summary>Pages whose text looks broken.</summary>
    public required int Suspect { get; init; }

    /// <summary>Pages with no text layer (scans).</summary>
    public required int NoText { get; init; }
}
