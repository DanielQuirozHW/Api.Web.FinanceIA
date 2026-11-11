// Project : Api.Web.FinanceIA
// Created : 2026-07-20

using System.ComponentModel.DataAnnotations;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Request body for POST /api/credit-lines/entity/{entityId}.
/// Creates the general credit line (cupo) for an entity in a single currency.
/// </summary>
public sealed class CreateCreditLineRequest
{
    /// <summary>Total approved amount of the cupo. Must be greater than zero.</summary>
    [Required]
    public decimal Amount { get; set; }

    /// <summary>ISO 4217 currency code (ARS, USD or EUR).</summary>
    [Required]
    public string Currency { get; set; } = string.Empty;

    /// <summary>
    /// [dbo].[Analyses].AnalysisId of the approved analysis that justifies creating this
    /// cupo. Required: it must exist, belong to this tenant and the same entity, and be
    /// Approved. Recorded on the new cupo as its origin analysis.
    /// </summary>
    [Required]
    public Guid AnalysisId { get; set; }
}
