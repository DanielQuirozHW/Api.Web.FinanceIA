// Project : Api.Web.FinanceIA
// Created : 2026-08-08

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Provenance read model: a cupo that a given analysis originated, surfaced on the analysis
/// detail response so the reverse of cupo → analysis is navigable. Deliberately minimal — just
/// enough to identify and link the cupo. Emitted regardless of the cupo's status (including
/// terminal 'Superseded'): this is provenance, not a liveness check.
/// </summary>
public sealed class OriginatedCreditLineResponse
{
    /// <summary>Credit line primary key.</summary>
    public Guid CreditLineId { get; set; }

    /// <summary>ISO 4217 currency code.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Total approved amount of the cupo.</summary>
    public decimal Amount { get; set; }

    /// <summary>Lifecycle status: 'Active', 'Superseded' or 'Suspended'.</summary>
    public string Status { get; set; } = string.Empty;
}
