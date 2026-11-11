// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-28

using System.Reflection;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Pins <see cref="Roles.SuperAdmin"/> as a READ-ONLY support role: it reads and exports everything
/// the <see cref="Roles.Auditor"/> does, keeps exactly its two operator writes (PUT /api/billing/config
/// and PUT /api/bff/settings/tenant-profile), and reaches no other write.
/// <para>
/// The named theories are the readable matrix; the two assembly scans are the guard. A write action
/// added later fails <see cref="EveryWriteAction_ExcludesSuperAdmin_ExceptItsTwoOperatorWrites"/>
/// unless it names a role gate without SuperAdmin — the class-level Internal scheme alone admits every
/// authenticated role, SuperAdmin included, so a forgotten gate is a SuperAdmin write.
/// </para>
/// <para>
/// Reflection over the declared <c>[Authorize]</c> metadata, like <see cref="AuthorizationMatrixTests"/>:
/// direct-call controller tests bypass the authorization middleware, so this is the coverage.
/// </para>
/// </summary>
public sealed class SuperAdminReadOnlyAuthorizationTests
{
    /// <summary>The only two writes SuperAdmin may perform.</summary>
    private static readonly HashSet<string> SuperAdminWrites = new(StringComparer.Ordinal)
    {
        Key(typeof(BillingController),  nameof(BillingController.UpdateConfigAsync)),
        Key(typeof(SettingsController), nameof(SettingsController.UpdateTenantProfile)),
    };

    /// <summary>
    /// Write-verb actions that declare NO role and are therefore open to every authenticated role.
    /// Each one is self-service (it touches only the caller's own row, session or MFA state) or a
    /// legacy Dynamics read that happens to be a POST; none writes another user's or the tenant's data.
    /// A new entry needs that argument — the tenant-wide alert read state is the counter-example and is
    /// gated on <see cref="Roles.AlertReadMarkers"/> instead.
    /// </summary>
    private static readonly HashSet<string> RoleLessWrites = new(StringComparer.Ordinal)
    {
        // Own notifications — the UserId is part of the repository predicate.
        Key(typeof(NotificationsController), nameof(NotificationsController.MarkAsReadAsync)),
        Key(typeof(NotificationsController), nameof(NotificationsController.MarkAllAsReadAsync)),
        Key(typeof(NotificationsController), nameof(NotificationsController.MarkAsUnreadAsync)),
        Key(typeof(NotificationsController), nameof(NotificationsController.MarkManyAsReadAsync)),
        // Own profile, own onboarding flags, own email verification, own MFA backup codes.
        Key(typeof(SettingsController), nameof(SettingsController.UpdateProfile)),
        Key(typeof(MeController), nameof(MeController.UpdateOnboardingFlagsAsync)),
        Key(typeof(EmailVerificationController), nameof(EmailVerificationController.RequestEmailVerificationAsync)),
        Key(typeof(MfaBackupCodesController), nameof(MfaBackupCodesController.GenerateAsync)),
        Key(typeof(MfaBackupCodesController), nameof(MfaBackupCodesController.ValidateAsync)),
        // Session: token exchange and refresh.
        Key(typeof(BffController), nameof(BffController.ExchangeFirebaseTokenAsync)),
        Key(typeof(BffController), nameof(BffController.RefreshAsync)),
        // Legacy Dynamics 365 queries (SmartJwt) — POST-shaped reads, no tenant-DB write.
        Key(typeof(SearchController), nameof(SearchController.SearchAsync)),
        Key(typeof(ConsultaFetchController), nameof(ConsultaFetchController.EjecutarFetchAsync)),
        Key(typeof(ConsultaFetchController), nameof(ConsultaFetchController.EjecutarFetchPaginadoAsync)),
    };

    // ── Reads and exports: allowed, with the Auditor ─────────────────────────────

    public static TheoryData<Type, string> AuditorReads => new()
    {
        { typeof(AnalysisController),           nameof(AnalysisController.GetAllAsync) },
        { typeof(ExportController),             nameof(ExportController.ExportPdfAsync) },
        { typeof(AlertRuleCatalogueController), nameof(AlertRuleCatalogueController.GetRulesAsync) },
        { typeof(AuditController),              nameof(AuditController.GetLogs) },
        { typeof(AuditController),              nameof(AuditController.GetFilters) },
        { typeof(CreditLinesController),        nameof(CreditLinesController.GetStructureAsync) },
        { typeof(FiadoresController),           nameof(FiadoresController.GetCandidatesAsync) },
        { typeof(FiadoresController),           nameof(FiadoresController.GetFiadoresAsync) },
        { typeof(SettingsController),           nameof(SettingsController.GetScoringConfig) },
        { typeof(SettingsController),           nameof(SettingsController.GetSectors) },
        { typeof(SettingsController),           nameof(SettingsController.GetSectorProfile) },
        { typeof(SettingsController),           nameof(SettingsController.GetTenantProfile) },
        { typeof(DashboardController),          nameof(DashboardController.GetPortfolioAsync) },
        { typeof(DashboardController),          nameof(DashboardController.GetOversightAsync) },
    };

    [Theory]
    [MemberData(nameof(AuditorReads))]
    public void AuditorRead_AllowsSuperAdmin_AndTheAuditor(Type controller, string method)
    {
        Assert.True(AuthorizationTestHelpers.RoleAllowed(controller, method, Roles.SuperAdmin));
        Assert.True(AuthorizationTestHelpers.RoleAllowed(controller, method, Roles.Auditor));
    }

    [Theory]
    [MemberData(nameof(AuditorReads))]
    public void AuditorRead_IsAGet(Type controller, string method)
    {
        Assert.Equal(new[] { "GET" }, VerbsOf(Method(controller, method)));
    }

    // ── Writes: denied ───────────────────────────────────────────────────────────

    public static TheoryData<Type, string> OutOfScopeActions => new()
    {
        // Creating, rescoring and deciding analyses; approval signatures and quorum votes.
        { typeof(AnalysisController),       nameof(AnalysisController.TriggerAsync) },
        { typeof(AnalysisController),       nameof(AnalysisController.RescoreAsync) },
        { typeof(AnalysisController),       nameof(AnalysisController.DecideAsync) },
        { typeof(AnalysisController),       nameof(AnalysisController.WithdrawApprovalVoteAsync) },
        { typeof(PersonAnalysesController), nameof(PersonAnalysesController.CreateAsync) },
        // The analysis chat (billed model calls).
        { typeof(AnalysisController), nameof(AnalysisController.ChatAsync) },
        { typeof(AnalysisController), nameof(AnalysisController.ChatAttachmentAsync) },
        { typeof(AnalysisController), nameof(AnalysisController.RenameConversationAsync) },
        // PyME writes, notes, ARCA lookups and vendor refreshes (billed).
        { typeof(EntitiesController), nameof(EntitiesController.CreateAsync) },
        { typeof(EntitiesController), nameof(EntitiesController.UpdateAsync) },
        { typeof(EntitiesController), nameof(EntitiesController.CreateNote) },
        { typeof(EntitiesController), nameof(EntitiesController.DeleteNote) },
        { typeof(EntitiesController), nameof(EntitiesController.LookupEntityFromArca) },
        { typeof(EntitiesController), nameof(EntitiesController.LookupCuitByDni) },
        { typeof(EntitiesController), nameof(EntitiesController.RefreshLufeAsync) },
        { typeof(EntitiesController), nameof(EntitiesController.RefreshBcraAsync) },
        { typeof(EntitiesController), nameof(EntitiesController.RefreshCasfogAsync) },
        { typeof(EntitiesController), nameof(EntitiesController.RefreshNosisAsync) },
        { typeof(EntitiesController), nameof(EntitiesController.ImportNosisReportAsync) },
        { typeof(EntitiesController), nameof(EntitiesController.DeleteNosisReportAsync) },
        { typeof(EntitiesController), nameof(EntitiesController.RefreshArcaAsync) },
        { typeof(EntitiesController), nameof(EntitiesController.ImportLufeDocumentAsync) },
        // Documents and the persona-humana legajo pipeline.
        { typeof(DocumentsController),            nameof(DocumentsController.UploadAsync) },
        { typeof(DocumentsController),            nameof(DocumentsController.DeleteAsync) },
        { typeof(DocumentsController),            nameof(DocumentsController.ReprocessAsync) },
        { typeof(DocumentsController),            nameof(DocumentsController.ConfirmClassificationAsync) },
        { typeof(PersonLegajosController),        nameof(PersonLegajosController.RecordConsentAsync) },
        { typeof(PersonLegajosController),        nameof(PersonLegajosController.RevokeConsentAsync) },
        { typeof(PersonLegajosController),        nameof(PersonLegajosController.RefreshBcraAsync) },
        { typeof(PersonLegajosController),        nameof(PersonLegajosController.RefreshArcaAsync) },
        { typeof(LegajoExtractionController),     nameof(LegajoExtractionController.CorrectAsync) },
        { typeof(LegajoExtractionController),     nameof(LegajoExtractionController.ReviewAsync) },
        { typeof(LegajoReconciliationController), nameof(LegajoReconciliationController.UpdateItemAsync) },
        { typeof(LegajoReconciliationController), nameof(LegajoReconciliationController.RebuildAsync) },
        // Credit lines and operations.
        { typeof(CreditLinesController), nameof(CreditLinesController.CreateCreditLineAsync) },
        { typeof(CreditLinesController), nameof(CreditLinesController.CreateOperationAsync) },
        { typeof(CreditLinesController), nameof(CreditLinesController.RaiseCreditLineAsync) },
        { typeof(CreditLinesController), nameof(CreditLinesController.SuspendCreditLineAsync) },
        { typeof(CreditLinesController), nameof(CreditLinesController.ReactivateCreditLineAsync) },
        { typeof(CreditLinesController), nameof(CreditLinesController.ConfirmOperationVigenciaAsync) },
        { typeof(CreditLinesController), nameof(CreditLinesController.CancelOperationAsync) },
        // Fiadores — including the billed ARCA prefill (a GET) and NOSIS refresh.
        { typeof(FiadoresController), nameof(FiadoresController.AddFiadorAsync) },
        { typeof(FiadoresController), nameof(FiadoresController.PatchFiadorAsync) },
        { typeof(FiadoresController), nameof(FiadoresController.RemoveFiadorAsync) },
        { typeof(FiadoresController), nameof(FiadoresController.RefreshFiadorNosisAsync) },
        { typeof(FiadoresController), nameof(FiadoresController.LinkFiadorLegajoAsync) },
        { typeof(FiadoresController), nameof(FiadoresController.ArcaLookupAsync) },
        // Alerts: resolving, reopening, and the TENANT-WIDE read state.
        { typeof(AlertsController), nameof(AlertsController.MarkAsResolvedAsync) },
        { typeof(AlertsController), nameof(AlertsController.UnresolveAsync) },
        { typeof(AlertsController), nameof(AlertsController.MarkAsReadAsync) },
        { typeof(AlertsController), nameof(AlertsController.MarkAllAsReadAsync) },
        // User and team management, approval limits.
        { typeof(SettingsController), nameof(SettingsController.GetUsers) },
        { typeof(SettingsController), nameof(SettingsController.InviteUser) },
        { typeof(SettingsController), nameof(SettingsController.GetInvitations) },
        { typeof(SettingsController), nameof(SettingsController.ResendInvitation) },
        { typeof(SettingsController), nameof(SettingsController.RevokeInvitation) },
        { typeof(SettingsController), nameof(SettingsController.GetUserApprovalLimits) },
        { typeof(SettingsController), nameof(SettingsController.SetUserApprovalLimits) },
        { typeof(SettingsController), nameof(SettingsController.SetUserTeam) },
        { typeof(TeamsController),    nameof(TeamsController.GetTeamsAsync) },
        { typeof(TeamsController),    nameof(TeamsController.CreateTeamAsync) },
        { typeof(TeamsController),    nameof(TeamsController.UpdateTeamAsync) },
        { typeof(TeamsController),    nameof(TeamsController.DeleteTeamAsync) },
        // Scoring, threshold, alert-rule and SGR settings edits.
        { typeof(SettingsController),           nameof(SettingsController.UpdateScoringConfig) },
        { typeof(SettingsController),           nameof(SettingsController.UpdateSectorProfile) },
        { typeof(SettingsController),           nameof(SettingsController.GetSgrSettings) },
        { typeof(SettingsController),           nameof(SettingsController.UpdateSgrSettings) },
        { typeof(AlertRuleCatalogueController), nameof(AlertRuleCatalogueController.UpdateRuleAsync) },
        // Decider work queues and the Admin operations dashboard — the Auditor has none of them either.
        { typeof(DashboardController), nameof(DashboardController.GetMyWorkAsync) },
        { typeof(DashboardController), nameof(DashboardController.GetApprovalsAsync) },
        { typeof(DashboardController), nameof(DashboardController.GetOperationsAsync) },
    };

    [Theory]
    [MemberData(nameof(OutOfScopeActions))]
    public void OutOfScopeAction_DeniesSuperAdmin(Type controller, string method)
    {
        var roles = AuthorizationTestHelpers.AuthorizeRolesOf(controller, method);

        Assert.NotEmpty(roles);
        Assert.DoesNotContain(Roles.SuperAdmin, roles);
    }

    [Theory]
    [InlineData(typeof(BillingController),  nameof(BillingController.UpdateConfigAsync))]
    [InlineData(typeof(SettingsController), nameof(SettingsController.UpdateTenantProfile))]
    public void OperatorWrite_StillAllowsExactlySuperAdmin(Type controller, string method)
    {
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { Roles.SuperAdmin },
            AuthorizationTestHelpers.AuthorizeRolesOf(controller, method));
    }

    // ── Assembly scans ───────────────────────────────────────────────────────────

    /// <summary>
    /// Every POST/PUT/PATCH/DELETE across the API is anonymous, or names a role gate that excludes
    /// SuperAdmin, or is one of SuperAdmin's two operator writes, or is a listed role-less
    /// self-service write. Nothing else.
    /// </summary>
    [Fact]
    public void EveryWriteAction_ExcludesSuperAdmin_ExceptItsTwoOperatorWrites()
    {
        var offenders = new List<string>();

        foreach (var (controller, method) in AllActions())
        {
            if (!VerbsOf(method).Any(IsWriteVerb) || IsAnonymous(controller, method))
                continue;

            var key   = Key(controller, method.Name);
            var roles = RolesOf(controller, method);

            if (roles.Count == 0)
            {
                if (!RoleLessWrites.Contains(key))
                    offenders.Add($"{key} has no role gate (open to SuperAdmin).");
            }
            else if (roles.Contains(Roles.SuperAdmin) && !SuperAdminWrites.Contains(key))
            {
                offenders.Add($"{key} admits SuperAdmin.");
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>The role-less allow-list does not rot: every entry exists, is a write, and is still role-less.</summary>
    [Fact]
    public void RoleLessWriteAllowList_IsCurrent()
    {
        var actual = AllActions()
            .Where(a => VerbsOf(a.Method).Any(IsWriteVerb)
                     && !IsAnonymous(a.Controller, a.Method)
                     && RolesOf(a.Controller, a.Method).Count == 0)
            .Select(a => Key(a.Controller, a.Method.Name))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(RoleLessWrites, actual);
    }

    /// <summary>
    /// Exact equivalence with the Auditor: every role-gated action SuperAdmin is admitted to is a GET
    /// the Auditor is also admitted to, or one of its two operator writes — and every role-gated GET
    /// the Auditor is admitted to admits SuperAdmin.
    /// </summary>
    [Fact]
    public void SuperAdminRoleGates_AreTheAuditorReads_PlusItsTwoOperatorWrites()
    {
        var offenders = new List<string>();

        foreach (var (controller, method) in AllActions())
        {
            var roles = RolesOf(controller, method);
            if (roles.Count == 0)
                continue;

            var key           = Key(controller, method.Name);
            var isGet         = VerbsOf(method).All(v => v == "GET");
            var hasAuditor    = roles.Contains(Roles.Auditor);
            var hasSuperAdmin = roles.Contains(Roles.SuperAdmin);

            if (hasSuperAdmin && !SuperAdminWrites.Contains(key) && !(isGet && hasAuditor))
                offenders.Add($"{key} admits SuperAdmin but is not an Auditor read.");

            if (hasAuditor && isGet && !hasSuperAdmin)
                offenders.Add($"{key} is an Auditor read that refuses SuperAdmin.");
        }

        Assert.Empty(offenders);
    }

    /// <summary><see cref="Roles.All"/> now carries SuperAdmin, so it must never gate a write.</summary>
    [Fact]
    public void EveryActionGatedOnAll_IsAGet()
    {
        var gatedOnAll = AllActions()
            .Where(a => string.Equals(DeclaredRolesOf(a.Method), Roles.All, StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(gatedOnAll);
        Assert.All(gatedOnAll, a => Assert.Equal(new[] { "GET" }, VerbsOf(a.Method)));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static IEnumerable<(Type Controller, MethodInfo Method)> AllActions() =>
        typeof(AnalysisController).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                              .Where(m => m.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any())
                              .Select(m => (t, m)));

    private static MethodInfo Method(Type controller, string method) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance).Single(m => m.Name == method);

    private static IReadOnlyList<string> VerbsOf(MethodInfo method) =>
        method.GetCustomAttributes<HttpMethodAttribute>(inherit: true)
              .SelectMany(a => a.HttpMethods)
              .Distinct(StringComparer.Ordinal)
              .ToList();

    private static bool IsWriteVerb(string verb) => verb is "POST" or "PUT" or "PATCH" or "DELETE";

    private static bool IsAnonymous(Type controller, MethodInfo method) =>
        method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any()
        || controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any();

    private static string? DeclaredRolesOf(MethodInfo method) =>
        method.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
              .Select(a => a.Roles)
              .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));

    private static HashSet<string> RolesOf(Type controller, MethodInfo method)
    {
        var roles = DeclaredRolesOf(method)
            ?? controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
                         .Select(a => a.Roles)
                         .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r))
            ?? string.Empty;

        return roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.Ordinal);
    }

    private static string Key(Type controller, string method) => $"{controller.Name}.{method}";
}
