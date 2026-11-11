// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-05-04

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Request body for POST /api/analyses.
/// </summary>
public sealed class TriggerAnalysisRequest
{
    /// <summary>
    /// Identifier of the document to analyse.
    /// Must belong to the current tenant and have ProcessingStatus = Pending.
    /// </summary>
    public Guid DocumentId { get; set; }

    /// <summary>
    /// Entity (PyME) the analysis belongs to.
    /// </summary>
    public Guid EntityId { get; set; }

    /// <summary>
    /// Fiscal year being analysed.
    /// </summary>
    public int FiscalYear { get; set; }

    /// <summary>
    /// Credit amount requested by the entity. Optional.
    /// </summary>
    public decimal? RequestedAmount { get; set; }
}
