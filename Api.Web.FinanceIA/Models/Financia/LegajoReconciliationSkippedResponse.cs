// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>An approved document a rebuild did not use.</summary>
public sealed record LegajoReconciliationSkippedResponse
{
    /// <summary>The document.</summary>
    public required Guid DocumentId { get; init; }

    /// <summary>superseded, amounts_unit_missing, older_snapshot, replaced or no_figures.</summary>
    public required string Reason { get; init; }
}
