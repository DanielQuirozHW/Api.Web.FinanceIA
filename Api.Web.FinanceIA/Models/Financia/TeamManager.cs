// Project : Api.Web.FinanceIA
// Created : 2026-09-16

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[TeamManagers] (DDL 064).
/// One row per manager of a team — a team may have several.
/// </summary>
/// <remarks>
/// This table exists because ONE manager is a single point of failure. DDL 062 gave an escalation an
/// addressee, but exactly one: the named manager goes on holiday and every pre-approval from that
/// team sits unactioned, because the RiskManager fallback only fires when the manager is absent or
/// deactivated, never when they are merely unavailable.
///
/// <para>
/// <see cref="Team.ManagerUserId"/> is NOT replaced by this table — it is RETAINED BUT DEPRECATED.
/// <c>TeamsController</c> still writes it (the existing single-manager Admin UI is unchanged in this
/// release) and mirrors the same person into a row here;
/// <see cref="Clases.AnalysisDecisionNotifier"/> reads the UNION of the two, de-duplicated. The union
/// is what keeps a tenant whose DDL 066 data migration was skipped routing to its named manager
/// instead of silently degrading to the fan-out. A later cleanup drops the column.
/// </para>
///
/// <para>
/// The key is COMPOSITE — (TeamId, UserId) — and there is no surrogate id: the pair is the whole
/// identity, nothing addresses one row of this table, and the clustered composite PK doubles as the
/// index for the only read it serves. See DDL 064 for the full argument.
/// </para>
/// </remarks>
public sealed class TeamManager
{
    /// <summary>The team. Part of the composite primary key.</summary>
    public Guid TeamId { get; set; }

    /// <summary>The user who manages it. Part of the composite primary key.</summary>
    public Guid UserId { get; set; }

    /// <summary>Tenant that owns this row. Firebase tenant slug, not a GUID.</summary>
    /// <remarks>
    /// Copied from the TEAM rather than the user: the row records "this team has this manager", so it
    /// belongs to the team's tenant. The two always agree — DDL 064's verification query 6 is what
    /// checks they keep agreeing.
    /// </remarks>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the manager was named.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// UserId of whoever named this manager, or null when the row was written by the DBA — which
    /// includes every row DDL 066 migrated from the deprecated <see cref="Team.ManagerUserId"/>.
    /// </summary>
    public Guid? CreatedBy { get; set; }
}
