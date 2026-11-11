// Project : Api.Web.FinanceIA
// Created : 2026-07-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Read model for an entity's full credit-line structure: every Active cupo
/// (one per currency), each with its operations and derived utilisation figures.
/// Superseded cupos are intentionally excluded.
/// </summary>
public sealed class CreditLineStructureResponse
{
    /// <summary>The entity's Active credit lines, ordered by currency.</summary>
    public IReadOnlyList<CreditLineResponse> CreditLines { get; set; } = [];
}
