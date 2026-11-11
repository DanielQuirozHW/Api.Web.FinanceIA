// Project : Api.Web.FinanceIA
// Created : 2026-09-04

using System.ComponentModel.DataAnnotations;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Request body for PUT /api/bff/settings/users/{userId}/approval-limits.
/// Carries the COMPLETE desired set of limits for that user — currencies omitted from
/// <see cref="Limits"/> are cleared, so this is a replace, not a merge.
/// </summary>
public sealed class SetUserApprovalLimitsRequest
{
    /// <summary>
    /// The complete set, at most one entry per currency. An empty list is legal and clears every
    /// limit the user had, returning them to the default of "may only pre-approve".
    /// </summary>
    [Required]
    public List<UserApprovalLimitEntry> Limits { get; set; } = [];
}

/// <summary>One currency's entry within <see cref="SetUserApprovalLimitsRequest"/>.</summary>
public sealed class UserApprovalLimitEntry
{
    /// <summary>ISO 4217 currency code (ARS, USD or EUR).</summary>
    [Required]
    public string Currency { get; set; } = string.Empty;

    /// <summary>Whether the user may approve directly in this currency.</summary>
    public bool CanApprove { get; set; }

    /// <summary>
    /// Inclusive ceiling for a direct approval; must not be negative. Null together with
    /// <see cref="CanApprove"/> = true grants an unlimited ceiling in this currency, which is why
    /// it is spelled as an explicit null rather than reached by omitting the field on a denial.
    /// </summary>
    public decimal? MaxAmount { get; set; }
}
