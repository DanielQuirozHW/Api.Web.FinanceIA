// Project : Api.Web.FinanceIA
// Created : 2026-08-07

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Request body for POST /api/credit-lines/{creditLineId}/suspend.
/// Suspends an Active cupo to stop new lending. All fields are optional.
/// </summary>
public sealed class SuspendCreditLineRequest
{
    /// <summary>Optional free-text reason for the suspension, recorded in the audit log payload.</summary>
    public string? Reason { get; set; }
}
