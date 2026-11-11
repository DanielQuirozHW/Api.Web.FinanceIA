// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>One figure of a period of GET /api/person-legajos/{id}/incomes.</summary>
public sealed record LegajoIncomeEntryResponse
{
    /// <summary>The row.</summary>
    public required Guid IncomeId { get; init; }

    /// <summary>One of LegajoIncomeSources.</summary>
    public required string Source { get; init; }

    /// <summary>One of LegajoIncomeConcepts.</summary>
    public required string Concept { get; init; }

    /// <summary>Pesos.</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO code (ARS).</summary>
    public required string Currency { get; init; }

    /// <summary>The document it was read from.</summary>
    public required Guid DocumentId { get; init; }

    /// <summary>The extraction run it was read from.</summary>
    public required Guid ExtractionId { get; init; }
}
