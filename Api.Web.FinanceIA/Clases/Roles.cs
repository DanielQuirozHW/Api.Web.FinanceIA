// Project : Api.Web.FinanceIA
// Created : 2026-07-29

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Canonical role names and role-group combinations for authorization.
/// Values mirror the role claim emitted by <see cref="ITokenService"/> exactly
/// (case-sensitive), so they are safe to use in <c>[Authorize(Roles = ...)]</c>.
/// Every member is a compile-time <c>const</c> — including the combinations,
/// which are constant string concatenations — so they are valid attribute arguments.
/// </summary>
public static class Roles
{
    /// <summary>
    /// Creates analyses; decides on them and sets credit lines. An Analyst's approval is bounded by
    /// their per-currency limit in <c>[dbo].[UserApprovalLimits]</c>: within it they approve
    /// directly, above it they can only PRE-approve, and they may never confirm a pre-approval.
    /// </summary>
    public const string Analyst = "Analyst";

    /// <summary>
    /// Creates analyses and reads scoring thresholds; cannot edit thresholds, manage users or view
    /// audit. CONFIRMS pre-approvals: on an analysis an Analyst pre-approved, a RiskManager approves
    /// (optionally changing the amount), rejects, or returns it for rework. No amount ceiling applies
    /// to them and <c>[dbo].[UserApprovalLimits]</c> is never consulted for them, so a decision they
    /// make on any analysis — pre-approved or not — is final in one step. As the supervising role it
    /// also MANAGES every cupo and operation of its tenant, whoever created them — see
    /// <see cref="CreditLineManagers"/>.
    /// </summary>
    public const string RiskManager = "RiskManager";

    /// <summary>Full access: everything the other roles can do, plus manage users, edit thresholds and view audit.</summary>
    public const string Admin = "Admin";

    /// <summary>
    /// Read-only oversight: view analyses/thresholds/audit and export; cannot create or decide.
    /// Nor can it edit, upload, refresh an integration, resolve an alert or use the analysis chat —
    /// every such route is gated on <see cref="Operators"/> or narrower. It may still mark alerts and
    /// its own notifications as read.
    /// <para>
    /// <see cref="SuperAdmin"/> mirrors this role's reads exactly: every reader constant that names
    /// Auditor also names SuperAdmin, so read-only support sees what oversight sees.
    /// </para>
    /// </summary>
    public const string Auditor = "Auditor";

    /// <summary>
    /// Cross-tenant READ-ONLY support role. It reads and exports everything the <see cref="Auditor"/>
    /// reads and exports — it sits in <see cref="All"/>, <see cref="CreditLineReaders"/>,
    /// <see cref="FiadorReaders"/>, <see cref="TenantConfigReaders"/>,
    /// <see cref="TenantConfigReadersWithOperator"/> and <see cref="AuditViewers"/> — and performs
    /// exactly two writes, both operator configuration rather than tenant data: the billing plan
    /// (PUT /api/billing/config) and the tenant profile (<see cref="TenantConfigWriters"/>).
    /// <para>
    /// It is in NO other write gate: not <see cref="Operators"/>, <see cref="AnalysisCreators"/>,
    /// <see cref="Deciders"/>, <see cref="CreditLineBuilders"/>, <see cref="CreditLineManagers"/>,
    /// <see cref="FiadorManagers"/> or any Admin-only gate — so no analysis, PyME, document, vendor
    /// refresh or lookup, chat, credit line, fiador, user, team, threshold or alert-rule change — and
    /// not <see cref="AlertReadMarkers"/>, because an alert's read state is shared by the whole tenant.
    /// <see cref="IsConfirmer"/> is false for it: it never signs a pre-approval or counts toward a quorum.
    /// </para>
    /// <para>
    /// Absent from <c>SettingsController.ValidRoles</c> and from the <c>CK_UserInvitations_Role</c>
    /// CHECK, so a client-portal Admin cannot invite one. Granted by the DBA, never from the portal;
    /// a user row can only hold it once DDL 076 has widened <c>CK_Users_Role</c>.
    /// </para>
    /// </summary>
    public const string SuperAdmin = "SuperAdmin";

    /// <summary>
    /// Roles allowed to create an analysis: <see cref="Analyst"/>, <see cref="RiskManager"/>,
    /// <see cref="Admin"/> (Auditor excluded).
    /// <para>
    /// <see cref="SuperAdmin"/> is deliberately EXCLUDED — it is read-only support and creates nothing;
    /// an analysis also spends billed vendor and model calls.
    /// </para>
    /// </summary>
    public const string AnalysisCreators = Analyst + "," + RiskManager + "," + Admin;

    /// <summary>
    /// Roles allowed at the decision endpoint (PATCH /api/analyses/{id}/decision):
    /// <see cref="Analyst"/> (decide, bounded by their approval limit),
    /// <see cref="RiskManager"/> and <see cref="Admin"/> (confirm a pre-approval, unbounded).
    /// Auditor is excluded — it is read-only oversight.
    /// <para>
    /// <see cref="SuperAdmin"/> is deliberately EXCLUDED — it is read-only support and decides nothing;
    /// nor is it a confirmer (<see cref="IsConfirmer"/>).
    /// </para>
    /// <para>
    /// This attribute is the OUTER gate only. Which of the three acts a caller may perform is
    /// decided inside <c>AnalysisController.DecideAsync</c> from the role on the user's DATABASE
    /// row, not from the token's claim — see the rationale there.
    /// </para>
    /// </summary>
    public const string Deciders = Analyst + "," + RiskManager + "," + Admin;

    /// <summary>
    /// Roles allowed to CREATE a cupo or an operation (POST /api/credit-lines/entity/{entityId},
    /// POST /api/credit-lines/{creditLineId}/operations): <see cref="Analyst"/>,
    /// <see cref="RiskManager"/>, <see cref="Admin"/>. Auditor is excluded — it neither decides nor builds.
    /// Reading the structure (GET /api/credit-lines) is the broader <see cref="CreditLineReaders"/>.
    /// <para>
    /// <see cref="SuperAdmin"/> is deliberately EXCLUDED — it is read-only support and builds no line,
    /// exactly like the Auditor.
    /// </para>
    /// <para>
    /// <see cref="RiskManager"/> is here because confirming a pre-approval is only half the act: the
    /// granted line still has to be built, and without this the confirmation flow dead-ends on a 403.
    /// Managing the line afterwards (raise, suspend, reactivate, confirmar-vigencia, cancel) is the
    /// separate <see cref="CreditLineManagers"/> policy.
    /// </para>
    /// <para>
    /// Same three names as <see cref="Deciders"/> today, kept as a separate constant on purpose: they
    /// are independent policies and either may move without the other.
    /// </para>
    /// </summary>
    public const string CreditLineBuilders = Analyst + "," + RiskManager + "," + Admin;

    /// <summary>
    /// Roles allowed at the cupo LIFECYCLE mutators — raise, suspend, reactivate, confirmar-vigencia
    /// and cancel (POST /api/credit-lines/{id}/raise, .../suspend, .../reactivate,
    /// .../operations/{operationId}/confirmar-vigencia, DELETE /api/credit-lines/operations/{operationId}):
    /// <see cref="Analyst"/>, <see cref="RiskManager"/>, <see cref="Admin"/>.
    /// <para>
    /// This attribute is the OUTER gate only. Which row a caller may touch is decided inside
    /// <c>CreditLinesController.CanMutate</c> from the role on the user's DATABASE row: its creator,
    /// or an <see cref="Admin"/> or <see cref="RiskManager"/> — the two tenant-wide roles. An
    /// <see cref="Analyst"/> still manages only the rows they created.
    /// </para>
    /// <para>
    /// <see cref="RiskManager"/> is here because it is the supervising role, and because leaving it
    /// out dead-ended the pre-approval flow: a RiskManager confirming a pre-approval CREATES the cupo
    /// (<see cref="CreditLineBuilders"/>), so the proposing analyst is not its creator and, without
    /// this, nobody but an Admin could ever raise, suspend or operate on it again.
    /// </para>
    /// <para>
    /// <see cref="Auditor"/> is deliberately EXCLUDED — read-only oversight changes nothing.
    /// </para>
    /// <para>
    /// <see cref="SuperAdmin"/> is deliberately EXCLUDED — it is read-only support and changes nothing,
    /// for the same reason.
    /// </para>
    /// <para>
    /// Same three names as <see cref="CreditLineBuilders"/> and <see cref="Deciders"/> today, kept as
    /// a separate constant on purpose: building a line and managing it are independent policies and
    /// either may move without the other.
    /// </para>
    /// </summary>
    public const string CreditLineManagers = Analyst + "," + RiskManager + "," + Admin;

    /// <summary>
    /// Roles allowed to READ an entity's credit-line structure (GET /api/credit-lines):
    /// <see cref="Analyst"/>, <see cref="RiskManager"/>, <see cref="Admin"/>, <see cref="Auditor"/>,
    /// <see cref="SuperAdmin"/>.
    /// <para>
    /// <see cref="Auditor"/> is here and ONLY here among the credit-line constants: read-only
    /// oversight may look at the cupos and operations granted on the analyses it oversees — as it
    /// already reads the fiadores backing them (<see cref="FiadorReaders"/>) and exports the PDF — and
    /// changes nothing about them. Every credit-line mutation stays on <see cref="CreditLineBuilders"/>
    /// or <see cref="CreditLineManagers"/>, both of which exclude it.
    /// </para>
    /// <para>
    /// <see cref="SuperAdmin"/> is here on the Auditor's terms: read-only support has to see the same
    /// cupos oversight sees to answer for them, and both mutation constants exclude it too.
    /// </para>
    /// <para>
    /// Reading is deliberately broader than building, the same shape as <see cref="FiadorReaders"/>
    /// against <see cref="FiadorManagers"/>. Kept as its own constant on purpose: credit-line access
    /// is an independent policy and may move without any of the others.
    /// </para>
    /// </summary>
    public const string CreditLineReaders = Analyst + "," + RiskManager + "," + Admin + "," + Auditor + "," + SuperAdmin;

    /// <summary>
    /// Roles allowed to READ the team roster (GET /api/bff/teams): <see cref="Analyst"/>,
    /// <see cref="RiskManager"/>, <see cref="Admin"/>.
    /// <para>
    /// Reading is deliberately broader than writing, which stays <see cref="Admin"/> alone: a
    /// <see cref="RiskManager"/> receives the escalated pre-approvals that are routed BY TEAM and has
    /// to be able to see which teams reach them, and an <see cref="Analyst"/> has to be able to see
    /// which team they belong to and who their manager is — the person their own pre-approval will go
    /// to. Every team MUTATION (create, update, delete) stays <see cref="Admin"/>: who reports to whom
    /// decides where approvals are routed, so moving it is user administration, not analysis work.
    /// </para>
    /// <para>
    /// <see cref="Auditor"/> is intentionally excluded. It does not participate in the approval-routing
    /// structure at all — it neither raises a pre-approval nor receives one — so the roster tells it
    /// nothing about the analyses it oversees.
    /// </para>
    /// <para>
    /// <see cref="SuperAdmin"/> is deliberately EXCLUDED — it is read-only support and mirrors the
    /// Auditor, which this constant excludes.
    /// </para>
    /// <para>
    /// Same three names as <see cref="AnalysisCreators"/>, <see cref="Deciders"/> and
    /// <see cref="CreditLineBuilders"/> today, kept as a separate constant on purpose: they are
    /// independent policies and any of them may move without the others.
    /// </para>
    /// </summary>
    public const string TeamReaders = Analyst + "," + RiskManager + "," + Admin;

    /// <summary>
    /// Roles allowed to VIEW an entity's fiadores — the candidate list and the fiador list
    /// (GET /api/entities/{entityId}/fiadores/candidates, GET /api/entities/{entityId}/fiadores):
    /// <see cref="Admin"/>, <see cref="Analyst"/>, <see cref="RiskManager"/>, <see cref="Auditor"/>,
    /// <see cref="SuperAdmin"/>.
    /// <para>
    /// <see cref="RiskManager"/> is here because it participates in the credit decision: it confirms
    /// the pre-approval and builds the line that follows (<see cref="CreditLineBuilders"/>, granted in
    /// BFF #193), and the fiadores are the guarantors backing that line. These routes simply missed
    /// that widening and gated the RiskManager out of the whole feature.
    /// </para>
    /// <para>
    /// <see cref="Auditor"/> is read-only and appears in THIS constant ONLY — it may look at who
    /// guarantees a line it oversees and change nothing about it. Every fiador MUTATION lives in
    /// <see cref="FiadorManagers"/>, which excludes it.
    /// </para>
    /// <para>
    /// <see cref="SuperAdmin"/> is here on exactly the Auditor's terms — read-only support — and
    /// <see cref="FiadorManagers"/> excludes it too.
    /// </para>
    /// <para>
    /// Reading is deliberately broader than managing, the same shape as
    /// <see cref="TenantConfigReaders"/> against <see cref="TenantConfigWriters"/>, and as
    /// <see cref="TeamReaders"/> against the Admin-only team mutations.
    /// </para>
    /// <para>
    /// Kept as its own constant on purpose even where the name list resembles another: fiador access
    /// is an independent policy and may move without any of them.
    /// </para>
    /// </summary>
    public const string FiadorReaders = Admin + "," + Analyst + "," + RiskManager + "," + Auditor + "," + SuperAdmin;

    /// <summary>
    /// Roles allowed to MANAGE an entity's fiadores — add (from LUFE or manually), edit, remove, refresh
    /// the fiador's own NOSIS snapshot, and look a CUIT up in ARCA to prefill a manual add
    /// (POST /api/entities/{entityId}/fiadores, PATCH and DELETE .../fiadores/{fiadorId},
    /// POST .../fiadores/{fiadorId}/refresh-nosis, GET .../fiadores/arca-lookup): <see cref="Admin"/>,
    /// <see cref="Analyst"/>, <see cref="RiskManager"/>. The ARCA prefill sits here, not in
    /// <see cref="FiadorReaders"/>, because it serves only the add form.
    /// <para>
    /// <see cref="Auditor"/> is deliberately EXCLUDED — it is a read-only role. It sees the fiadores
    /// through <see cref="FiadorReaders"/> and touches none of them; refresh-nosis in particular is a
    /// billed vendor query, which read-only oversight has no business spending.
    /// </para>
    /// <para>
    /// <see cref="SuperAdmin"/> is deliberately EXCLUDED — it is read-only support and spends no billed
    /// NOSIS or ARCA query, for the same reason.
    /// </para>
    /// <para>
    /// <see cref="RiskManager"/> is included for the same reason as in <see cref="FiadorReaders"/>:
    /// choosing which socios guarantee a line is part of the credit decision it takes part in, not
    /// administration performed afterwards.
    /// </para>
    /// <para>
    /// Same three names as <see cref="CreditLineBuilders"/> and <see cref="Deciders"/> today, kept as a
    /// separate constant on purpose: these are independent policies and any of them may move without
    /// the others.
    /// </para>
    /// </summary>
    public const string FiadorManagers = Admin + "," + Analyst + "," + RiskManager;

    /// <summary>
    /// Roles that OPERATE on the tenant's working data — PyMEs, their notes, documents, vendor
    /// integrations, alerts and the analysis chat: <see cref="Analyst"/>, <see cref="RiskManager"/>,
    /// <see cref="Admin"/>. The general-purpose write gate for every such route that has no narrower
    /// policy of its own: creating and editing an entity, the ARCA lookups, the refresh-* vendor calls,
    /// importing a LUFE document, uploading and deleting documents, resolving an alert, and sending,
    /// attaching to or renaming an analysis chat.
    /// <para>
    /// <see cref="Auditor"/> is deliberately EXCLUDED — it is read-only oversight. It reads all of the
    /// above and changes none of it; several of these routes (refresh-*, lookup-arca, chat) also spend
    /// billed vendor or model calls, which oversight has no business spending.
    /// </para>
    /// <para>
    /// <see cref="SuperAdmin"/> is deliberately EXCLUDED — it is the cross-tenant platform operator
    /// (billing plan, tenant profile), not a tenant operator, and works no PyME.
    /// </para>
    /// <para>
    /// Same three names as <see cref="AnalysisCreators"/>, <see cref="Deciders"/>,
    /// <see cref="CreditLineBuilders"/> and <see cref="FiadorManagers"/> today, kept as a separate
    /// constant on purpose: those are specific policies and any of them may move without this one.
    /// </para>
    /// </summary>
    public const string Operators = Analyst + "," + RiskManager + "," + Admin;

    /// <summary>
    /// Roles allowed to READ tenant-level configuration: <see cref="Admin"/>,
    /// <see cref="RiskManager"/>, <see cref="Auditor"/>, <see cref="SuperAdmin"/>.
    /// <see cref="Analyst"/> is excluded — an analyst runs analyses, they do not inspect how the
    /// tenant is configured.
    /// <para>
    /// Reading is deliberately broader than writing, which stays <see cref="Admin"/> alone: a
    /// RiskManager has to be able to see the parameters their decisions rest on, and an Auditor to
    /// check them, without either being able to move them.
    /// </para>
    /// <para>
    /// Used by the scoring-config reads (GET scoring-config, scoring-config/sectors and
    /// scoring-config/sectors/{sector}) and the tenant-wide dashboards (GET dashboard/portfolio and
    /// dashboard/oversight).
    /// </para>
    /// <para>
    /// <see cref="SuperAdmin"/> is here as read-only support, on the Auditor's terms: it may check the
    /// parameters a score rests on and move none of them — every threshold write is Admin-only.
    /// </para>
    /// <para>
    /// Same names as <see cref="TenantConfigReadersWithOperator"/> today, kept as a separate constant on
    /// purpose: that one includes SuperAdmin because it WRITES the setting it reads, this one because
    /// SuperAdmin supports the tenant. Either may move without the other, so a new endpoint should
    /// pick between them deliberately rather than reach for whichever name is shorter.
    /// </para>
    /// </summary>
    public const string TenantConfigReaders = Admin + "," + RiskManager + "," + Auditor + "," + SuperAdmin;

    /// <summary>
    /// Roles allowed to READ tenant-level configuration that the cross-tenant operator also sets:
    /// <see cref="TenantConfigReaders"/> plus <see cref="SuperAdmin"/>.
    /// <para>
    /// Used by the tenant-profile GET. <see cref="SuperAdmin"/> is here for one reason: it is the
    /// only role that may WRITE that profile (<see cref="TenantConfigWriters"/>), and a role that
    /// can set a value it cannot read back is a nonsense permission. That reason holds whether or not
    /// SuperAdmin is also a support reader (<see cref="TenantConfigReaders"/>), which is why the two
    /// constants stay separate even while their names match.
    /// </para>
    /// </summary>
    public const string TenantConfigReadersWithOperator =
        Admin + "," + RiskManager + "," + Auditor + "," + SuperAdmin;

    /// <summary>
    /// Roles allowed to WRITE the tenant profile — the <c>sgr</c> / <c>non_sgr</c> entity-type
    /// switch (PUT /api/bff/settings/tenant-profile): <see cref="SuperAdmin"/> alone.
    /// <para>
    /// That switch is OPERATOR configuration, not the client tenant's: it decides which external
    /// vendors are CALLED during enrichment, so moving it commits the tenant to per-query vendor
    /// billing. It therefore sits with the same cross-tenant role that owns the tenant plan
    /// (PUT /api/billing/config), not with the tenant's own <see cref="Admin"/>.
    /// </para>
    /// <para>
    /// Deliberately narrower than <see cref="TenantConfigReadersWithOperator"/>: an
    /// <see cref="Admin"/>, <see cref="RiskManager"/> or <see cref="Auditor"/> still SEES the
    /// profile, because it explains the enrichment an analysis carries — they just cannot move it.
    /// </para>
    /// </summary>
    public const string TenantConfigWriters = SuperAdmin;

    /// <summary>
    /// Every role — the four client roles plus <see cref="SuperAdmin"/> — for READ/EXPORT endpoints
    /// open to every authenticated role, stated explicitly to document intent.
    /// <para>
    /// READ ONLY. It names the read-only <see cref="Auditor"/> and <see cref="SuperAdmin"/>, so a
    /// write must never use it; <c>SuperAdminReadOnlyAuthorizationTests</c> fails any non-GET action
    /// that does. Today it gates GET /api/analyses, GET /api/bff/alert-rules and the PDF export.
    /// </para>
    /// </summary>
    public const string All = Analyst + "," + RiskManager + "," + Admin + "," + Auditor + "," + SuperAdmin;

    /// <summary>
    /// Roles allowed to VIEW the audit log (GET /api/bff/audit/logs and /filters): <see cref="Admin"/>,
    /// <see cref="Auditor"/>, <see cref="SuperAdmin"/>.
    /// <para>
    /// <see cref="SuperAdmin"/> is here as read-only support, on the Auditor's terms. The log is
    /// append-only and these routes only read it. <see cref="RiskManager"/> and <see cref="Analyst"/>
    /// stay out, as before.
    /// </para>
    /// </summary>
    public const string AuditViewers = Admin + "," + Auditor + "," + SuperAdmin;

    /// <summary>
    /// Roles allowed to READ the persona humana calibration board and export it
    /// (GET /api/dashboard/person-calibration and .../export): <see cref="Admin"/>, <see cref="RiskManager"/>.
    /// <para>
    /// The board exists for the SGR's risk leads to review the pilot's thresholds with the client: who
    /// approved against a Rechazo sugerido, which filters lack data, which documents arrive stale. It names
    /// every analyst and their reasons, so it is a supervising read, not analysis work: <see cref="Analyst"/>
    /// is excluded.
    /// </para>
    /// <para>
    /// <see cref="Auditor"/> is excluded by product decision: the board drives a threshold review, which is
    /// not oversight of decided analyses. <see cref="SuperAdmin"/> mirrors the Auditor's reads (BFF #278 —
    /// <c>SuperAdminReadOnlyAuthorizationTests</c> holds a GET to both or neither), so it is excluded too.
    /// </para>
    /// <para>
    /// Kept as its own constant on purpose: the board's audience is an independent policy and may move
    /// without any other.
    /// </para>
    /// </summary>
    public const string CalibrationReaders = Admin + "," + RiskManager;

    /// <summary>
    /// Roles allowed to MARK alerts read (PATCH /api/bff/alerts/{alertId}/read,
    /// POST /api/bff/alerts/mark-all-read): <see cref="Analyst"/>, <see cref="RiskManager"/>,
    /// <see cref="Admin"/>, <see cref="Auditor"/> — the four client roles.
    /// <para>
    /// The read state is TENANT-WIDE — one <c>IsRead</c> column on the alert row, not one per user —
    /// so marking an alert read clears it for everyone in the tenant. The Auditor keeps it by product
    /// decision. <see cref="SuperAdmin"/> is deliberately EXCLUDED: read-only support looking at a
    /// tenant must not clear the alerts the tenant's own users have not seen yet.
    /// </para>
    /// </summary>
    public const string AlertReadMarkers = Analyst + "," + RiskManager + "," + Admin + "," + Auditor;

    /// <summary>
    /// Whether a role may CONFIRM a pre-approval — supply one of the signatures a pre-approved
    /// analysis is waiting for. <see cref="RiskManager"/> and <see cref="Admin"/>; nobody else,
    /// including the <see cref="Analyst"/> who pre-approved it, the <see cref="Auditor"/> and the
    /// read-only <see cref="SuperAdmin"/>.
    /// </summary>
    /// <remarks>
    /// A method rather than a const list because two places must agree on it and the consequence of
    /// their disagreeing is severe. <c>AnalysisController.DecideAsync</c> uses it to decide who may
    /// act on a pre-approval, and <c>AnalysisApprovalQuorumService</c> uses it to decide who COUNTS
    /// toward the quorum. If the second were wider than the first, the effective quorum would
    /// include people the endpoint refuses — and the analysis would wait forever for a signature
    /// nobody is able to give.
    ///
    /// <para>
    /// Compared ordinally, like every other role comparison in this codebase, because these values
    /// mirror the role claim exactly and a culture-aware match is not what "same role" means.
    /// </para>
    /// </remarks>
    /// <param name="role">The role from the user's DATABASE row — not from the token claim.</param>
    public static bool IsConfirmer(string? role)
        => string.Equals(role, RiskManager, StringComparison.Ordinal)
        || string.Equals(role, Admin, StringComparison.Ordinal);
}
