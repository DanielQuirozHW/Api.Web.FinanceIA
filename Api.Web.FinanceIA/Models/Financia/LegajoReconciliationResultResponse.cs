// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>POST /api/person-legajos/{id}/reconciliation/rebuild: what the rebuild did.</summary>
public sealed record LegajoReconciliationResultResponse
{
    /// <summary>The legajo.</summary>
    public required Guid PersonLegajoId { get; init; }

    /// <summary>Items after the rebuild.</summary>
    public required int ItemsCount { get; init; }

    /// <summary>Incomes after the rebuild.</summary>
    public required int IncomesCount { get; init; }

    /// <summary>Items created.</summary>
    public required int ItemsAdded { get; init; }

    /// <summary>Kept items whose values changed.</summary>
    public required int ItemsUpdated { get; init; }

    /// <summary>Items no document states any more.</summary>
    public required int ItemsRemoved { get; init; }

    /// <summary>Confirmed conflicts that changed or vanished, back to their computed status.</summary>
    public required int ConfirmationsLapsed { get; init; }

    /// <summary>The documents that contributed.</summary>
    public required IReadOnlyList<Guid> DocumentsUsed { get; init; }

    /// <summary>The approved documents that contributed nothing, each with its reason.</summary>
    public required IReadOnlyList<LegajoReconciliationSkippedResponse> DocumentsSkipped { get; init; }
}
