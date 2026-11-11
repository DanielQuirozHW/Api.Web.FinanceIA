// Project : Api.Web.FinanceIA
// Created : 2026-09-04

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One configured approval limit, as returned by
/// GET/PUT /api/bff/settings/users/{userId}/approval-limits.
/// </summary>
public sealed class UserApprovalLimitResponse
{
    /// <summary>ISO 4217 currency code this limit applies to.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Whether the user may approve directly in this currency.</summary>
    public bool CanApprove { get; set; }

    /// <summary>
    /// Inclusive ceiling for a direct approval. Null together with <see cref="CanApprove"/> = true
    /// means no ceiling in this currency.
    /// </summary>
    public decimal? MaxAmount { get; set; }

    /// <summary>UTC timestamp of the most recent update to this limit.</summary>
    public DateTime UpdatedAt { get; set; }
}
