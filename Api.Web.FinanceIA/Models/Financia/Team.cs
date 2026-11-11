// Project : Api.Web.FinanceIA
// Created : 2026-09-10

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[Teams].
/// Represents a group of users within a tenant and the manager who runs it.
/// </summary>
/// <remarks>
/// The team exists so that an escalation has an ADDRESSEE. When an analyst pre-approves an analysis
/// they cannot commit alone, the notification goes to the manager of their team rather than to every
/// RiskManager in the tenant.
///
/// <para>
/// A team may name SEVERAL managers, and they live in [dbo].[TeamManagers] (DDL 064), not here.
/// <see cref="ManagerUserId"/> is a retained-but-deprecated mirror of the first of them.
/// </para>
///
/// <para>
/// An EMPTY manager set is meaningful: a team may exist before anyone has been named to run it. The
/// emitter reads that as "no manager to notify" and falls back to the tenant's active RiskManagers —
/// the safe reading, because a pre-approval that reaches nobody loses the work while one that reaches
/// too many is only noise.
/// </para>
/// </remarks>
public sealed class Team
{
    /// <summary>Primary key. Supplied by the application — see TeamConfiguration.</summary>
    public Guid TeamId { get; set; }

    /// <summary>Tenant that owns this team. Firebase tenant slug, not a GUID.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Display name shown in the Admin screen.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// RETAINED BUT DEPRECATED mirror of the FIRST manager in [dbo].[TeamManagers]. Null when the
    /// team has none — see the remarks on this class for what that triggers.
    /// </summary>
    /// <remarks>
    /// <c>TeamsController</c> keeps this in step on every create and update, and
    /// <c>TeamManagerResolution</c> UNIONS it with [dbo].[TeamManagers] on read. Both exist for the
    /// same reason: DDL 066 is a DATA script and the <c>--verify-schema</c> gate compares columns, not
    /// rows, so a tenant whose 066 was skipped would pass every check and silently lose its manager.
    /// The Phase-3 cleanup drops the column and both halves of this bridge with it. Nothing new should
    /// read it.
    /// </remarks>
    public Guid? ManagerUserId { get; set; }

    /// <summary>
    /// How many manager signatures a pre-approval from this team needs before it is confirmed
    /// (DDL 065). Defaults to 1, which is exactly today's behaviour: one signature confirms.
    /// </summary>
    /// <remarks>
    /// ENFORCED by <c>AnalysisApprovalQuorumService</c>, which counts votes against it (DDL 067), and
    /// WRITABLE through <c>TeamsController</c>'s POST/PUT since the multi-manager release.
    ///
    /// <para>
    /// This is the CONFIGURED value, and the engine never uses it raw: the effective quorum is
    /// <c>MAX(1, MIN(this, eligible active managers))</c>. The cap means a 3 on a two-manager team is
    /// legal and settles on two signatures rather than stranding the analysis; the floor exists
    /// because DDL 065 ships no CHECK constraint, so a 0 written by hand would otherwise confirm an
    /// analysis on no signatures at all. The API refuses anything below 1 on the way in.
    /// </para>
    ///
    /// <para>
    /// The <c>= 1</c> initializer is load-bearing, not decoration. <c>TeamsController</c> builds
    /// <c>new Team { ... }</c> without naming this property, and EF sends every mapped property on
    /// an INSERT — so without it EF would write an explicit 0 and silently override the column's
    /// DEFAULT (1). The value matches DF_Teams_ApprovalQuorum in DDL 065 and must stay in step
    /// with it.
    /// </para>
    /// </remarks>
    public int ApprovalQuorum { get; set; } = 1;

    /// <summary>UTC timestamp when the team was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UserId of whoever created the team, or null when seeded by the DBA.</summary>
    public Guid? CreatedBy { get; set; }

    /// <summary>UTC timestamp of the most recent update to this record.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>UserId of whoever last updated the team, or null when seeded by the DBA.</summary>
    public Guid? UpdatedBy { get; set; }
}
