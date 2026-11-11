// Project : Api.Web.FinanceIA.Tests
// Created : 2026-07-29

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Full role-authorization matrix for the four roles (Analyst, RiskManager, Admin,
/// Auditor) across every gated endpoint. Each row asserts allow/deny against the
/// declared <c>[Authorize(Roles=...)]</c> metadata (via <see cref="AuthorizationTestHelpers"/>).
/// This is the comprehensive lock; the per-area tests (threshold, users, audit) narrow in
/// on individual slices. The RiskManager row is called out explicitly as the highest risk.
/// </summary>
public sealed class RoleMatrixAuthorizationTests
{
    [Theory]
    // ── Create analysis (POST /api/analyses): Analyst, RiskManager, Admin — Auditor denied ──
    [InlineData(typeof(AnalysisController), nameof(AnalysisController.TriggerAsync), Roles.Analyst,     true)]
    [InlineData(typeof(AnalysisController), nameof(AnalysisController.TriggerAsync), Roles.RiskManager, true)]
    [InlineData(typeof(AnalysisController), nameof(AnalysisController.TriggerAsync), Roles.Admin,       true)]
    [InlineData(typeof(AnalysisController), nameof(AnalysisController.TriggerAsync), Roles.Auditor,     false)]

    // ── View all (GET /api/analyses): all four allowed, plus read-only SuperAdmin ──
    [InlineData(typeof(AnalysisController), nameof(AnalysisController.GetAllAsync), Roles.Analyst,     true)]
    [InlineData(typeof(AnalysisController), nameof(AnalysisController.GetAllAsync), Roles.RiskManager, true)]
    [InlineData(typeof(AnalysisController), nameof(AnalysisController.GetAllAsync), Roles.Admin,       true)]
    [InlineData(typeof(AnalysisController), nameof(AnalysisController.GetAllAsync), Roles.Auditor,     true)]
    [InlineData(typeof(AnalysisController), nameof(AnalysisController.GetAllAsync), Roles.SuperAdmin,  true)]

    // ── Decide (PATCH /api/analyses/{id}/decision): Analyst, RiskManager, Admin — Auditor denied ──
    // RiskManager is ALLOWED here as of the two-step decision flow, reversing the earlier rule. The
    // endpoint now carries three different acts and the attribute is only the outer gate: an Analyst
    // decides (bounded by their per-currency approval limit, so an amount above it becomes a
    // pre-approval rather than an approval), while RiskManager and Admin CONFIRM, reject or return a
    // pre-approval. Which act a caller gets is settled inside DecideAsync from the role on their
    // DATABASE row — an Analyst confirming a pre-approval is a 403 from there, not from here.
    // Auditor stays denied: read-only oversight decides nothing.
    [InlineData(typeof(AnalysisController), nameof(AnalysisController.DecideAsync), Roles.Analyst,     true)]
    [InlineData(typeof(AnalysisController), nameof(AnalysisController.DecideAsync), Roles.Admin,       true)]
    [InlineData(typeof(AnalysisController), nameof(AnalysisController.DecideAsync), Roles.RiskManager, true)]
    [InlineData(typeof(AnalysisController), nameof(AnalysisController.DecideAsync), Roles.Auditor,     false)]

    // ── Approval limits (GET/PUT /api/bff/settings/users/{id}/approval-limits): Admin only ──
    // Setting how much an analyst may commit without a second signature is the control the whole
    // two-step flow rests on; a RiskManager who could raise the ceiling of the analysts they review
    // would dissolve it.
    [InlineData(typeof(SettingsController), nameof(SettingsController.GetUserApprovalLimits), Roles.Admin,       true)]
    [InlineData(typeof(SettingsController), nameof(SettingsController.GetUserApprovalLimits), Roles.Analyst,     false)]
    [InlineData(typeof(SettingsController), nameof(SettingsController.GetUserApprovalLimits), Roles.RiskManager, false)]
    [InlineData(typeof(SettingsController), nameof(SettingsController.GetUserApprovalLimits), Roles.Auditor,     false)]
    [InlineData(typeof(SettingsController), nameof(SettingsController.SetUserApprovalLimits), Roles.Admin,       true)]
    [InlineData(typeof(SettingsController), nameof(SettingsController.SetUserApprovalLimits), Roles.Analyst,     false)]
    [InlineData(typeof(SettingsController), nameof(SettingsController.SetUserApprovalLimits), Roles.RiskManager, false)]
    [InlineData(typeof(SettingsController), nameof(SettingsController.SetUserApprovalLimits), Roles.Auditor,     false)]

    // ── Teams: READ open to Analyst/RiskManager/Admin, MUTATIONS Admin only ──
    // Who reports to whom decides where a pre-approval is ROUTED, so changing it is the same class
    // of control as the approval limits above: a RiskManager who could reassign the analysts they
    // review — or make themselves the manager of a team — would be choosing their own workload.
    // The LISTING is a different question and is deliberately wider: a RiskManager has to see which
    // teams escalate to them, and an analyst which team they are in and who their manager is.
    // Auditor is denied on all four — it does not participate in the approval-routing structure.
    [InlineData(typeof(TeamsController), nameof(TeamsController.GetTeamsAsync),   Roles.Admin,       true)]
    [InlineData(typeof(TeamsController), nameof(TeamsController.GetTeamsAsync),   Roles.Analyst,     true)]
    [InlineData(typeof(TeamsController), nameof(TeamsController.GetTeamsAsync),   Roles.RiskManager, true)]
    [InlineData(typeof(TeamsController), nameof(TeamsController.GetTeamsAsync),   Roles.Auditor,     false)]
    [InlineData(typeof(TeamsController), nameof(TeamsController.CreateTeamAsync), Roles.Admin,       true)]
    [InlineData(typeof(TeamsController), nameof(TeamsController.CreateTeamAsync), Roles.Analyst,     false)]
    [InlineData(typeof(TeamsController), nameof(TeamsController.CreateTeamAsync), Roles.RiskManager, false)]
    [InlineData(typeof(TeamsController), nameof(TeamsController.CreateTeamAsync), Roles.Auditor,     false)]
    [InlineData(typeof(TeamsController), nameof(TeamsController.UpdateTeamAsync), Roles.Admin,       true)]
    [InlineData(typeof(TeamsController), nameof(TeamsController.UpdateTeamAsync), Roles.Analyst,     false)]
    [InlineData(typeof(TeamsController), nameof(TeamsController.UpdateTeamAsync), Roles.RiskManager, false)]
    [InlineData(typeof(TeamsController), nameof(TeamsController.UpdateTeamAsync), Roles.Auditor,     false)]
    [InlineData(typeof(TeamsController), nameof(TeamsController.DeleteTeamAsync), Roles.Admin,       true)]
    [InlineData(typeof(TeamsController), nameof(TeamsController.DeleteTeamAsync), Roles.Analyst,     false)]
    [InlineData(typeof(TeamsController), nameof(TeamsController.DeleteTeamAsync), Roles.RiskManager, false)]
    [InlineData(typeof(TeamsController), nameof(TeamsController.DeleteTeamAsync), Roles.Auditor,     false)]

    // ── Assign a user to a team (PUT /api/bff/settings/users/{id}/team): Admin only ──
    // Same control as the Teams rows above, reached from the per-user side.
    [InlineData(typeof(SettingsController), nameof(SettingsController.SetUserTeam), Roles.Admin,       true)]
    [InlineData(typeof(SettingsController), nameof(SettingsController.SetUserTeam), Roles.Analyst,     false)]
    [InlineData(typeof(SettingsController), nameof(SettingsController.SetUserTeam), Roles.RiskManager, false)]
    [InlineData(typeof(SettingsController), nameof(SettingsController.SetUserTeam), Roles.Auditor,     false)]

    // ── Export PDF (GET /api/bff/analyses/{id}/export/pdf): all four allowed, plus read-only SuperAdmin ──
    [InlineData(typeof(ExportController), nameof(ExportController.ExportPdfAsync), Roles.Analyst,     true)]
    [InlineData(typeof(ExportController), nameof(ExportController.ExportPdfAsync), Roles.RiskManager, true)]
    [InlineData(typeof(ExportController), nameof(ExportController.ExportPdfAsync), Roles.Admin,       true)]
    [InlineData(typeof(ExportController), nameof(ExportController.ExportPdfAsync), Roles.Auditor,     true)]
    [InlineData(typeof(ExportController), nameof(ExportController.ExportPdfAsync), Roles.SuperAdmin,  true)]

    // ── Manage users – list (GET /api/bff/settings/users): Admin only ──
    [InlineData(typeof(SettingsController), nameof(SettingsController.GetUsers), Roles.Admin,       true)]
    [InlineData(typeof(SettingsController), nameof(SettingsController.GetUsers), Roles.Analyst,     false)]
    [InlineData(typeof(SettingsController), nameof(SettingsController.GetUsers), Roles.RiskManager, false)]
    [InlineData(typeof(SettingsController), nameof(SettingsController.GetUsers), Roles.Auditor,     false)]

    // ── Manage users – invite (POST /api/bff/settings/users/invite): Admin only ──
    [InlineData(typeof(SettingsController), nameof(SettingsController.InviteUser), Roles.Admin,       true)]
    [InlineData(typeof(SettingsController), nameof(SettingsController.InviteUser), Roles.Analyst,     false)]
    [InlineData(typeof(SettingsController), nameof(SettingsController.InviteUser), Roles.RiskManager, false)]
    [InlineData(typeof(SettingsController), nameof(SettingsController.InviteUser), Roles.Auditor,     false)]

    // ── Read thresholds (GET /api/bff/settings/scoring-config): Admin, RiskManager, Auditor, SuperAdmin — Analyst denied ──
    [InlineData(typeof(SettingsController), nameof(SettingsController.GetScoringConfig), Roles.Admin,       true)]
    [InlineData(typeof(SettingsController), nameof(SettingsController.GetScoringConfig), Roles.RiskManager, true)]
    [InlineData(typeof(SettingsController), nameof(SettingsController.GetScoringConfig), Roles.Auditor,     true)]
    [InlineData(typeof(SettingsController), nameof(SettingsController.GetScoringConfig), Roles.SuperAdmin,  true)]
    [InlineData(typeof(SettingsController), nameof(SettingsController.GetScoringConfig), Roles.Analyst,     false)]

    // ── Edit thresholds (PUT /api/bff/settings/scoring-config): Admin only ──
    [InlineData(typeof(SettingsController), nameof(SettingsController.UpdateScoringConfig), Roles.Admin,       true)]
    [InlineData(typeof(SettingsController), nameof(SettingsController.UpdateScoringConfig), Roles.Analyst,     false)]
    [InlineData(typeof(SettingsController), nameof(SettingsController.UpdateScoringConfig), Roles.RiskManager, false)]
    [InlineData(typeof(SettingsController), nameof(SettingsController.UpdateScoringConfig), Roles.Auditor,     false)]

    // ── View audit log (GET /api/bff/audit/logs): Admin, Auditor, SuperAdmin — Analyst & RiskManager denied ──
    [InlineData(typeof(AuditController), nameof(AuditController.GetLogs), Roles.Admin,       true)]
    [InlineData(typeof(AuditController), nameof(AuditController.GetLogs), Roles.Auditor,     true)]
    [InlineData(typeof(AuditController), nameof(AuditController.GetLogs), Roles.SuperAdmin,  true)]
    [InlineData(typeof(AuditController), nameof(AuditController.GetLogs), Roles.Analyst,     false)]
    [InlineData(typeof(AuditController), nameof(AuditController.GetLogs), Roles.RiskManager, false)]

    // ── Read alert rules (GET /api/bff/alert-rules): all four allowed, plus read-only SuperAdmin ──
    // Deliberately NOT the scoring-config matrix, which excludes Analyst. Which rules exist and at
    // what threshold is the analyst's working information: it is what explains a catalogue alert on
    // a report they are deciding on.
    [InlineData(typeof(AlertRuleCatalogueController), nameof(AlertRuleCatalogueController.GetRulesAsync), Roles.Analyst,     true)]
    [InlineData(typeof(AlertRuleCatalogueController), nameof(AlertRuleCatalogueController.GetRulesAsync), Roles.RiskManager, true)]
    [InlineData(typeof(AlertRuleCatalogueController), nameof(AlertRuleCatalogueController.GetRulesAsync), Roles.Admin,       true)]
    [InlineData(typeof(AlertRuleCatalogueController), nameof(AlertRuleCatalogueController.GetRulesAsync), Roles.Auditor,     true)]
    [InlineData(typeof(AlertRuleCatalogueController), nameof(AlertRuleCatalogueController.GetRulesAsync), Roles.SuperAdmin,  true)]

    // ── Edit alert rules (PATCH /api/bff/alert-rules/{id}): Admin only ──
    // A threshold change alters which alerts fire and therefore what the SGR decides on.
    [InlineData(typeof(AlertRuleCatalogueController), nameof(AlertRuleCatalogueController.UpdateRuleAsync), Roles.Admin,       true)]
    [InlineData(typeof(AlertRuleCatalogueController), nameof(AlertRuleCatalogueController.UpdateRuleAsync), Roles.Analyst,     false)]
    [InlineData(typeof(AlertRuleCatalogueController), nameof(AlertRuleCatalogueController.UpdateRuleAsync), Roles.RiskManager, false)]
    [InlineData(typeof(AlertRuleCatalogueController), nameof(AlertRuleCatalogueController.UpdateRuleAsync), Roles.Auditor,     false)]

    // ── Confirm operation vigencia (POST .../operations/{id}/confirmar-vigencia): Analyst, RiskManager, Admin ──
    // Declaring an operation to be in force is an underwriting act, not a reporting one: it is the
    // moment the SGR states the granted line is real. The RiskManager, as the supervising role that
    // also creates the cupo when it confirms a pre-approval, may perform it (Roles.CreditLineManagers);
    // the Auditor observes and does not make credit vigente.
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.ConfirmOperationVigenciaAsync), Roles.Analyst,     true)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.ConfirmOperationVigenciaAsync), Roles.Admin,       true)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.ConfirmOperationVigenciaAsync), Roles.RiskManager, true)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.ConfirmOperationVigenciaAsync), Roles.Auditor,     false)]

    // ── Cancel operation (DELETE .../operations/{id}): Analyst, RiskManager, Admin ──
    // The mirror of confirming: recording that a line fell through ("se cayó") or was closed retires
    // live exposure and frees cupo. Same underwriting act, same three roles.
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.CancelOperationAsync), Roles.Analyst,     true)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.CancelOperationAsync), Roles.Admin,       true)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.CancelOperationAsync), Roles.RiskManager, true)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.CancelOperationAsync), Roles.Auditor,     false)]

    // ── Read the cupo structure (GET /api/credit-lines): Analyst, RiskManager, Admin, Auditor, SuperAdmin ──
    // ── Create a cupo (POST /api/credit-lines/entity/{id}): Analyst, RiskManager, Admin ──────
    // ── Create an operation (POST /api/credit-lines/{id}/operations): same three ──────────────
    // Confirming a pre-approval is only half the act: the granted line still has to be built, and the
    // RM who confirmed is the one who builds it. None of the three consults CanMutate — the read is a
    // read, and the two POSTs are creations with no owner yet to compare against.
    // Auditor may READ the structure (Roles.CreditLineReaders), as it reads the fiadores and the
    // PDF, and is denied both creations: read-only oversight builds nothing.
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.GetStructureAsync),     Roles.Analyst,     true)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.GetStructureAsync),     Roles.RiskManager, true)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.GetStructureAsync),     Roles.Admin,       true)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.GetStructureAsync),     Roles.Auditor,     true)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.GetStructureAsync),     Roles.SuperAdmin,  true)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.CreateCreditLineAsync), Roles.Analyst,     true)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.CreateCreditLineAsync), Roles.RiskManager, true)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.CreateCreditLineAsync), Roles.Admin,       true)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.CreateCreditLineAsync), Roles.Auditor,     false)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.CreateOperationAsync),  Roles.Analyst,     true)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.CreateOperationAsync),  Roles.RiskManager, true)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.CreateOperationAsync),  Roles.Admin,       true)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.CreateOperationAsync),  Roles.Auditor,     false)]

    // ── Cupo lifecycle (raise / suspend / reactivate): Analyst, RiskManager, Admin ───────────
    // Changing the size or the standing of a cupo after the fact is cupo MANAGEMENT, gated with
    // confirmar-vigencia and cancel on Roles.CreditLineManagers plus CanMutate (creator, Admin or
    // RiskManager). Leaving the RiskManager out dead-ended the pre-approval flow: the RM creates the
    // cupo when confirming, so the proposing analyst is not its creator and only an Admin could
    // ever manage it.
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.RaiseCreditLineAsync),      Roles.Analyst,     true)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.RaiseCreditLineAsync),      Roles.Admin,       true)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.RaiseCreditLineAsync),      Roles.RiskManager, true)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.RaiseCreditLineAsync),      Roles.Auditor,     false)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.SuspendCreditLineAsync),    Roles.Analyst,     true)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.SuspendCreditLineAsync),    Roles.Admin,       true)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.SuspendCreditLineAsync),    Roles.RiskManager, true)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.SuspendCreditLineAsync),    Roles.Auditor,     false)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.ReactivateCreditLineAsync), Roles.Analyst,     true)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.ReactivateCreditLineAsync), Roles.Admin,       true)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.ReactivateCreditLineAsync), Roles.RiskManager, true)]
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.ReactivateCreditLineAsync), Roles.Auditor,     false)]

    // ── Fiadores READ (GET .../fiadores/candidates, GET .../fiadores): all four + SuperAdmin ──
    // The guarantors back the line the RiskManager confirms and then builds, so seeing who they are
    // is part of the same credit decision — these routes had gated it out of the feature entirely.
    // Auditor is allowed HERE and nowhere else in this controller: it may look at who guarantees a
    // line it oversees and change nothing about it. SuperAdmin (read-only support) reads on the same terms.
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.GetCandidatesAsync), Roles.Admin,       true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.GetCandidatesAsync), Roles.Analyst,     true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.GetCandidatesAsync), Roles.RiskManager, true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.GetCandidatesAsync), Roles.Auditor,     true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.GetCandidatesAsync), Roles.SuperAdmin,  true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.GetFiadoresAsync),   Roles.Admin,       true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.GetFiadoresAsync),   Roles.Analyst,     true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.GetFiadoresAsync),   Roles.RiskManager, true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.GetFiadoresAsync),   Roles.Auditor,     true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.GetFiadoresAsync),   Roles.SuperAdmin,  true)]

    // ── Fiadores MANAGE (add / edit / remove / refresh-nosis / arca-lookup): Admin, Analyst, RiskManager
    // Choosing which socios guarantee the line is part of the decision, not administration after it,
    // so the RiskManager keeps the door the reads opened. Auditor is denied on all three: it is
    // read-only, and refresh-nosis in particular spends a per-query NOSIS billing unit.
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.AddFiadorAsync),          Roles.Admin,       true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.AddFiadorAsync),          Roles.Analyst,     true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.AddFiadorAsync),          Roles.RiskManager, true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.AddFiadorAsync),          Roles.Auditor,     false)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.RemoveFiadorAsync),       Roles.Admin,       true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.RemoveFiadorAsync),       Roles.Analyst,     true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.RemoveFiadorAsync),       Roles.RiskManager, true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.RemoveFiadorAsync),       Roles.Auditor,     false)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.RefreshFiadorNosisAsync), Roles.Admin,       true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.RefreshFiadorNosisAsync), Roles.Analyst,     true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.RefreshFiadorNosisAsync), Roles.RiskManager, true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.RefreshFiadorNosisAsync), Roles.Auditor,     false)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.PatchFiadorAsync),        Roles.Admin,       true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.PatchFiadorAsync),        Roles.Analyst,     true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.PatchFiadorAsync),        Roles.RiskManager, true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.PatchFiadorAsync),        Roles.Auditor,     false)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.ArcaLookupAsync),         Roles.Admin,       true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.ArcaLookupAsync),         Roles.Analyst,     true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.ArcaLookupAsync),         Roles.RiskManager, true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.ArcaLookupAsync),         Roles.Auditor,     false)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.LinkFiadorLegajoAsync),   Roles.Admin,       true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.LinkFiadorLegajoAsync),   Roles.Analyst,     true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.LinkFiadorLegajoAsync),   Roles.RiskManager, true)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.LinkFiadorLegajoAsync),   Roles.Auditor,     false)]
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.LinkFiadorLegajoAsync),   Roles.SuperAdmin,  false)]
    public void Endpoint_Role_MatchesMatrix(Type controller, string method, string role, bool allowed)
    {
        Assert.Equal(allowed, AuthorizationTestHelpers.RoleAllowed(controller, method, role));
    }

    // ── Exact-set guards for the alert rule catalogue ────────────────────────────
    // Catch a widened writer set or a narrowed reader set wholesale, not just role by role.

    [Fact]
    public void ReadAlertRules_RoleSet_IsExact()
    {
        var roles = AuthorizationTestHelpers.AuthorizeRolesOf(
            typeof(AlertRuleCatalogueController), nameof(AlertRuleCatalogueController.GetRulesAsync));
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
                { Roles.Analyst, Roles.RiskManager, Roles.Admin, Roles.Auditor, Roles.SuperAdmin },
            roles);
    }

    [Fact]
    public void EditAlertRules_RoleSet_IsExact()
    {
        var roles = AuthorizationTestHelpers.AuthorizeRolesOf(
            typeof(AlertRuleCatalogueController), nameof(AlertRuleCatalogueController.UpdateRuleAsync));
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { Roles.Admin },
            roles);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Highest-risk row: RiskManager. Called out explicitly so a regression that grants
    // RiskManager a forbidden action fails a named, obvious test.
    // ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(typeof(SettingsController),  nameof(SettingsController.UpdateScoringConfig))]    // edit thresholds
    [InlineData(typeof(SettingsController),  nameof(SettingsController.GetUsers))]               // manage users (list)
    [InlineData(typeof(SettingsController),  nameof(SettingsController.InviteUser))]             // manage users (invite)
    [InlineData(typeof(SettingsController),  nameof(SettingsController.GetUserApprovalLimits))]  // read approval limits
    [InlineData(typeof(SettingsController),  nameof(SettingsController.SetUserApprovalLimits))]  // set approval limits
    [InlineData(typeof(AuditController),     nameof(AuditController.GetLogs))]                    // view audit
    [InlineData(typeof(AlertRuleCatalogueController), nameof(AlertRuleCatalogueController.UpdateRuleAsync))] // edit alert rules
    public void RiskManager_IsDenied_OnPrivilegedActions(Type controller, string method)
    {
        Assert.False(AuthorizationTestHelpers.RoleAllowed(controller, method, Roles.RiskManager));
    }

    [Theory]
    [InlineData(typeof(AnalysisController),  nameof(AnalysisController.TriggerAsync))]           // create
    [InlineData(typeof(AnalysisController),  nameof(AnalysisController.GetAllAsync))]            // view all
    [InlineData(typeof(AnalysisController),  nameof(AnalysisController.DecideAsync))]            // confirm a pre-approval
    [InlineData(typeof(ExportController),    nameof(ExportController.ExportPdfAsync))]           // export
    [InlineData(typeof(SettingsController),  nameof(SettingsController.GetScoringConfig))]       // read thresholds
    // Every credit-line endpoint: the three a confirmation needs to build the line, and the five
    // lifecycle mutators — the supervising role manages every cupo of its tenant, including the one
    // its own confirmation created (Roles.CreditLineManagers + CanMutate).
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.GetStructureAsync))]     // read the cupo structure
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.CreateCreditLineAsync))] // create the cupo
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.CreateOperationAsync))]  // create the operation
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.RaiseCreditLineAsync))]           // raise a cupo
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.SuspendCreditLineAsync))]         // suspend a cupo
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.ReactivateCreditLineAsync))]      // reactivate a cupo
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.ConfirmOperationVigenciaAsync))]  // make an operation vigente
    [InlineData(typeof(CreditLinesController), nameof(CreditLinesController.CancelOperationAsync))]           // cancel an operation
    // All five fiadores endpoints. The guarantors are part of the line the RiskManager confirms and
    // builds, so both the reads and the three mutations are open to it — the 403 this fixes.
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.GetCandidatesAsync))]      // list candidates
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.GetFiadoresAsync))]        // list fiadores
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.AddFiadorAsync))]          // add a fiador
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.RemoveFiadorAsync))]       // remove a fiador
    [InlineData(typeof(FiadoresController), nameof(FiadoresController.RefreshFiadorNosisAsync))] // refresh its NOSIS
    [InlineData(typeof(AnalysisController), nameof(AnalysisController.WithdrawApprovalVoteAsync))] // retract their own approval
    public void RiskManager_IsAllowed_OnPermittedActions(Type controller, string method)
    {
        Assert.True(AuthorizationTestHelpers.RoleAllowed(controller, method, Roles.RiskManager));
    }

    /// <summary>
    /// The decision endpoint's role set, exactly. Guards the two directions the theory rows above
    /// cannot: a widened set (Auditor slipping in) and a narrowed one (RiskManager dropped again,
    /// which would silently strand every pre-approved analysis with nobody able to confirm it).
    /// </summary>
    [Fact]
    public void Decide_RoleSet_IsExact()
    {
        var roles = AuthorizationTestHelpers.AuthorizeRolesOf(
            typeof(AnalysisController), nameof(AnalysisController.DecideAsync));
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
                { Roles.Analyst, Roles.RiskManager, Roles.Admin },
            roles);
    }

    /// <summary>
    /// The withdraw endpoint's role set, exactly, and it is the SAME set the decision endpoint
    /// carries. Both directions matter. Widened, an Auditor — read-only oversight — could reach into
    /// a credit decision. Narrowed to the two confirming roles alone it would look tighter and be
    /// wrong: the outer attribute is only the door, and refusing an Analyst here with a 403 from the
    /// framework instead of from the action would diverge from how DecideAsync answers the same
    /// caller. Who may actually withdraw is decided inside, on the role from the DATABASE row.
    /// </summary>
    [Fact]
    public void WithdrawApprovalVote_RoleSet_IsExact()
    {
        var roles = AuthorizationTestHelpers.AuthorizeRolesOf(
            typeof(AnalysisController), nameof(AnalysisController.WithdrawApprovalVoteAsync));
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
                { Roles.Analyst, Roles.RiskManager, Roles.Admin },
            roles);
    }

    /// <summary>
    /// The two credit-line creations a RiskManager needs to build the line their confirmation
    /// produced, as an exact set. Guards both directions the theory rows cannot: a widened set
    /// (Auditor slipping into a write path) and a narrowed one (RiskManager dropped again, which
    /// silently strands every confirmed pre-approval with nobody able to build its cupo). The
    /// structure READ it also needs is the wider <see cref="Roles.CreditLineReaders"/>, pinned in
    /// <see cref="AuthorizationMatrixTests"/>.
    /// </summary>
    [Theory]
    [InlineData(nameof(CreditLinesController.CreateCreditLineAsync))]
    [InlineData(nameof(CreditLinesController.CreateOperationAsync))]
    public void CreditLineBuilders_RoleSet_IsExact(string method)
    {
        var roles = AuthorizationTestHelpers.AuthorizeRolesOf(typeof(CreditLinesController), method);
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
                { Roles.Analyst, Roles.RiskManager, Roles.Admin },
            roles);
    }

    /// <summary>
    /// The five cupo lifecycle mutators, as an exact set (<see cref="Roles.CreditLineManagers"/>).
    /// Guards both directions: <see cref="Roles.Auditor"/> slipping into cupo management, and
    /// <see cref="Roles.RiskManager"/> dropped again — which brings back the dead end where a cupo
    /// created by a RiskManager's confirmation can be managed by an Admin alone.
    /// </summary>
    [Theory]
    [InlineData(nameof(CreditLinesController.RaiseCreditLineAsync))]
    [InlineData(nameof(CreditLinesController.SuspendCreditLineAsync))]
    [InlineData(nameof(CreditLinesController.ReactivateCreditLineAsync))]
    [InlineData(nameof(CreditLinesController.ConfirmOperationVigenciaAsync))]
    [InlineData(nameof(CreditLinesController.CancelOperationAsync))]
    public void CreditLineMutators_RoleSet_IsExact(string method)
    {
        var roles = AuthorizationTestHelpers.AuthorizeRolesOf(typeof(CreditLinesController), method);
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
                { Roles.Analyst, Roles.RiskManager, Roles.Admin },
            roles);
    }

    /// <summary>
    /// The two fiadores reads, as an exact set. Guards both directions the theory rows cannot: a
    /// narrowed set (RiskManager or Auditor dropped, the 403 this policy fixes coming back) and a
    /// widened one — <see cref="Roles.Auditor"/> belongs in the read set and nowhere else, so this is
    /// the only place its presence is asserted.
    /// </summary>
    [Theory]
    [InlineData(nameof(FiadoresController.GetCandidatesAsync))]
    [InlineData(nameof(FiadoresController.GetFiadoresAsync))]
    public void FiadorReaders_RoleSet_IsExact(string method)
    {
        var roles = AuthorizationTestHelpers.AuthorizeRolesOf(typeof(FiadoresController), method);
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
                { Roles.Analyst, Roles.RiskManager, Roles.Admin, Roles.Auditor, Roles.SuperAdmin },
            roles);
    }

    /// <summary>
    /// The three fiadores mutators, as an exact set. The mirror of
    /// <see cref="FiadorReaders_RoleSet_IsExact"/>: this is the half that must NOT move when the read
    /// set does, so a future widening of the reads cannot leak <see cref="Roles.Auditor"/> into a
    /// write path — least of all refresh-nosis, which spends a billed vendor query.
    /// </summary>
    [Theory]
    [InlineData(nameof(FiadoresController.AddFiadorAsync))]
    [InlineData(nameof(FiadoresController.RemoveFiadorAsync))]
    [InlineData(nameof(FiadoresController.RefreshFiadorNosisAsync))]
    [InlineData(nameof(FiadoresController.LinkFiadorLegajoAsync))]
    public void FiadorManagers_RoleSet_IsExact(string method)
    {
        var roles = AuthorizationTestHelpers.AuthorizeRolesOf(typeof(FiadoresController), method);
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
                { Roles.Analyst, Roles.RiskManager, Roles.Admin },
            roles);
    }
}
