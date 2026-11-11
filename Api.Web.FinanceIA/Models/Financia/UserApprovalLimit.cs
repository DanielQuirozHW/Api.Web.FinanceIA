// Project : Api.Web.FinanceIA
// Created : 2026-09-04

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[UserApprovalLimits] (DDL 059).
/// How much one user may commit directly, in one currency, before a second signature is required.
/// </summary>
/// <remarks>
/// <para>
/// One row per (user, currency). The ABSENCE of a row is meaningful and is the default: a user with
/// no row for a currency cannot approve in it directly at all, only pre-approve. This is why the
/// table is not seeded — a tenant that never configures it simply routes every analyst decision
/// through a RiskManager, which is the safe reading of "not configured".
/// </para>
/// <para>
/// Consulted for <see cref="Api.Web.FinanceIA.Clases.Roles.Analyst"/> only. RiskManager and Admin
/// have no ceiling and never read this table, so they have no rows here either — an empty result
/// for them is not a restriction.
/// </para>
/// </remarks>
public sealed class UserApprovalLimit
{
    /// <summary>Tenant that owns this limit. Firebase tenant slug, never a GUID.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>User the limit applies to. Half of the composite primary key.</summary>
    public Guid UserId { get; set; }

    /// <summary>ISO 4217 currency code. The other half of the composite primary key.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>
    /// Whether this user may approve directly in this currency. False makes the row an explicit
    /// denial — distinct from no row at all only in that it is a deliberate, auditable record.
    /// </summary>
    public bool CanApprove { get; set; }

    /// <summary>
    /// Ceiling for a direct approval, inclusive. NULL together with
    /// <see cref="CanApprove"/> = true means no ceiling in this currency.
    /// </summary>
    public decimal? MaxAmount { get; set; }

    /// <summary>UTC timestamp when the row was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Admin who created the row. Null when seeded by the DBA.</summary>
    public Guid? CreatedBy { get; set; }

    /// <summary>UTC timestamp of the most recent update.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Admin who last updated the row. Null when seeded by the DBA.</summary>
    public Guid? UpdatedBy { get; set; }
}
