// Project : Api.Web.FinanceIA
// Created : 2026-07-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[CreditLines].
/// The general credit line (cupo) for an entity in a single currency. Exactly one
/// row is Active per (TenantId, EntityId, Currency). The cupo amount is raised
/// in place (an ampliación updates <see cref="Amount"/> on the same row and stamps
/// <see cref="LastRaisedAt"/>/<see cref="LastRaiseAnalysisId"/>) — operations are
/// never re-parented. 'Superseded' and 'Suspended' remain valid status values in the
/// schema but are not produced by any current endpoint.
/// Database First — schema changes via DDL scripts only.
/// </summary>
public sealed class CreditLine
{
    /// <summary>Primary key (database-assigned via NEWSEQUENTIALID()).</summary>
    public Guid CreditLineId { get; set; }

    /// <summary>Tenant that owns this credit line.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Foreign key to [dbo].[Entities].EntityId — the PyME this cupo belongs to.</summary>
    public Guid EntityId { get; set; }

    /// <summary>Total approved amount of the cupo.</summary>
    public decimal Amount { get; set; }

    /// <summary>ISO 4217 currency code (e.g. 'ARS', 'USD').</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>
    /// Lifecycle status: 'Active', 'Superseded' or 'Suspended'. 'Suspended' means the
    /// cupo accepts no new operations or raises while its existing operations remain
    /// valid; no endpoint produces it yet (reserved for a later suspension feature).
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>[dbo].[Users].UserId that created this credit line.</summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>UTC timestamp when the credit line was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// UTC timestamp of the most recent ampliación (raise), or null if the cupo has
    /// never been raised. Together with <see cref="CreatedAt"/> it forms the freshness
    /// baseline: a raise requires an analysis created after COALESCE(LastRaisedAt, CreatedAt).
    /// </summary>
    public DateTime? LastRaisedAt { get; set; }

    /// <summary>
    /// [dbo].[Analyses].AnalysisId of the approved analysis that justifies the cupo's
    /// current amount, set on each raise. Null until the cupo is first raised.
    /// </summary>
    public Guid? LastRaiseAnalysisId { get; set; }

    /// <summary>
    /// [dbo].[Analyses].AnalysisId of the approved analysis that justified this cupo's
    /// CREATION — recorded once at creation and never changed. Distinct from
    /// <see cref="LastRaiseAnalysisId"/>, which tracks the most recent ampliación (raise).
    /// Null for cupos created before this field existed (no backfill); required by the
    /// API for every cupo created from now on.
    /// </summary>
    public Guid? OriginAnalysisId { get; set; }
}
