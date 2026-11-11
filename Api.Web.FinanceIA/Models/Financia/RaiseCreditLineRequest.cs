// Project : Api.Web.FinanceIA
// Created : 2026-08-03

using System.ComponentModel.DataAnnotations;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Request body for POST /api/credit-lines/{creditLineId}/raise.
/// Raises (amplía) an Active cupo's amount in place. The new amount must be strictly
/// greater than the current amount, and the raise must be backed by an Approved
/// analysis created after the cupo's last raise (or its creation, if never raised).
/// </summary>
public sealed class RaiseCreditLineRequest
{
    /// <summary>New total amount of the cupo. Must be strictly greater than the current amount.</summary>
    [Required]
    public decimal NewAmount { get; set; }

    /// <summary>Id of the Approved analysis that justifies the raise.</summary>
    [Required]
    public Guid AnalysisId { get; set; }
}
