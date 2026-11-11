// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-24

using System.Reflection;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Pins the Auditor-is-read-only matrix: which constant every write action names, that the
/// credit-line READ is open to Auditor while its five lifecycle mutators stay on
/// <see cref="Roles.CreditLineManagers"/> (which excludes it), and
/// that the anonymous test endpoints and the dead invite route are gone.
/// <para>
/// These are reflection tests over the declared <c>[Authorize]</c> metadata. Direct-call controller
/// tests bypass the authorization middleware, so an attribute gate never fires in them — this file
/// is the coverage for the gates themselves.
/// </para>
/// </summary>
public sealed class AuthorizationMatrixTests
{
    // ── Write actions gated on Roles.Operators ───────────────────────────────────

    public static TheoryData<Type, string> OperatorActions => new()
    {
        // EntitiesController — PyME create/edit, notes, ARCA lookups, vendor refreshes, LUFE import.
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
        { typeof(EntitiesController), nameof(EntitiesController.RefreshArcaAsync) },
        { typeof(EntitiesController), nameof(EntitiesController.ImportLufeDocumentAsync) },
        // DDL 087 — the NOSIS Manager report import and its removal.
        { typeof(EntitiesController), nameof(EntitiesController.ImportNosisReportAsync) },
        { typeof(EntitiesController), nameof(EntitiesController.DeleteNosisReportAsync) },
        // DocumentsController — upload and delete.
        { typeof(DocumentsController), nameof(DocumentsController.UploadAsync) },
        { typeof(DocumentsController), nameof(DocumentsController.DeleteAsync) },
        // DocumentsController — the legajo pipeline (DDL 070): requeue a failed document, confirm a
        // classification under review.
        { typeof(DocumentsController), nameof(DocumentsController.ReprocessAsync) },
        { typeof(DocumentsController), nameof(DocumentsController.ConfirmClassificationAsync) },
        // PersonLegajosController — recording and revoking the data-processing authorization.
        { typeof(PersonLegajosController), nameof(PersonLegajosController.RecordConsentAsync) },
        { typeof(PersonLegajosController), nameof(PersonLegajosController.RevokeConsentAsync) },
        // PersonLegajosController — the legajo-scoped vendor refreshes (billed), the same gate as the
        // entity refreshes they mirror (M10 Phase 5: a fiador's legajo has no entity route).
        { typeof(PersonLegajosController), nameof(PersonLegajosController.RefreshBcraAsync) },
        { typeof(PersonLegajosController), nameof(PersonLegajosController.RefreshArcaAsync) },
        // LegajoExtractionController — the extraction review's corrections and approval (Phase 2b).
        { typeof(LegajoExtractionController), nameof(LegajoExtractionController.CorrectAsync) },
        { typeof(LegajoExtractionController), nameof(LegajoExtractionController.ReviewAsync) },
        // LegajoReconciliationController — an analyst's edit of an inventory item, and the explicit rebuild (3b).
        { typeof(LegajoReconciliationController), nameof(LegajoReconciliationController.UpdateItemAsync) },
        { typeof(LegajoReconciliationController), nameof(LegajoReconciliationController.RebuildAsync) },
        // AlertsController — resolving or reopening an alert is an operator act; marking it read is
        // AlertReadMarkers (pinned below).
        { typeof(AlertsController), nameof(AlertsController.MarkAsResolvedAsync) },
        { typeof(AlertsController), nameof(AlertsController.UnresolveAsync) },
        // AnalysisController — the analysis chat: send, attach, rename.
        { typeof(AnalysisController), nameof(AnalysisController.ChatAsync) },
        { typeof(AnalysisController), nameof(AnalysisController.ChatAttachmentAsync) },
        { typeof(AnalysisController), nameof(AnalysisController.RenameConversationAsync) },
    };

    [Theory]
    [MemberData(nameof(OperatorActions))]
    public void WriteAction_DeclaresOperators(Type controller, string method)
    {
        Assert.Equal(Roles.Operators, AuthorizationTestHelpers.DeclaredRolesOf(controller, method));
    }

    [Theory]
    [MemberData(nameof(OperatorActions))]
    public void WriteAction_DeniesAuditorAndSuperAdmin(Type controller, string method)
    {
        Assert.False(AuthorizationTestHelpers.RoleAllowed(controller, method, Roles.Auditor));
        Assert.False(AuthorizationTestHelpers.RoleAllowed(controller, method, Roles.SuperAdmin));
    }

    [Theory]
    [MemberData(nameof(OperatorActions))]
    public void WriteAction_AllowsTheThreeOperatorRoles(Type controller, string method)
    {
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { Roles.Analyst, Roles.RiskManager, Roles.Admin },
            AuthorizationTestHelpers.AuthorizeRolesOf(controller, method));
    }

    /// <summary>
    /// A write action added to EntitiesController or DocumentsController later must not ship with
    /// only the class-level Internal scheme — the gap this matrix closes. Every POST, PUT, PATCH and
    /// DELETE there declares a role gate, and none of those gates admits Auditor.
    /// </summary>
    [Theory]
    [InlineData(typeof(EntitiesController))]
    [InlineData(typeof(DocumentsController))]
    [InlineData(typeof(PersonLegajosController))]
    [InlineData(typeof(LegajoExtractionController))]
    [InlineData(typeof(LegajoReconciliationController))]
    public void EveryWriteAction_IsRoleGated_AndExcludesAuditor(Type controller)
    {
        var writes = WriteActionsOf(controller);
        Assert.NotEmpty(writes);

        foreach (var method in writes)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(AuthorizationTestHelpers.DeclaredRolesOf(controller, method.Name)),
                $"{controller.Name}.{method.Name} has no [Authorize(Roles = ...)].");
            Assert.False(
                AuthorizationTestHelpers.RoleAllowed(controller, method.Name, Roles.Auditor),
                $"{controller.Name}.{method.Name} admits Auditor.");
        }
    }

    /// <summary>
    /// Marking an alert read stays open to the four client roles, Auditor included (product
    /// decision), and is closed to SuperAdmin. The read state is tenant-wide — one IsRead column on
    /// the alert row — so read-only support marking it would clear it for the tenant's own users.
    /// </summary>
    [Theory]
    [InlineData(nameof(AlertsController.MarkAsReadAsync))]
    [InlineData(nameof(AlertsController.MarkAllAsReadAsync))]
    public void AlertReadActions_DeclareAlertReadMarkers_AndRefuseSuperAdmin(string method)
    {
        Assert.Equal(Roles.AlertReadMarkers, AuthorizationTestHelpers.DeclaredRolesOf(typeof(AlertsController), method));
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
                { Roles.Analyst, Roles.RiskManager, Roles.Admin, Roles.Auditor },
            AuthorizationTestHelpers.AuthorizeRolesOf(typeof(AlertsController), method));
        Assert.False(AuthorizationTestHelpers.RoleAllowed(typeof(AlertsController), method, Roles.SuperAdmin));
    }

    // ── Credit lines ─────────────────────────────────────────────────────────────

    [Fact]
    public void CreditLineStructureRead_DeclaresCreditLineReaders_IncludingAuditorAndSuperAdmin()
    {
        var method = nameof(CreditLinesController.GetStructureAsync);

        Assert.Equal(
            Roles.CreditLineReaders,
            AuthorizationTestHelpers.DeclaredRolesOf(typeof(CreditLinesController), method));
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
                { Roles.Analyst, Roles.RiskManager, Roles.Admin, Roles.Auditor, Roles.SuperAdmin },
            AuthorizationTestHelpers.AuthorizeRolesOf(typeof(CreditLinesController), method));
    }

    /// <summary>
    /// The five cupo lifecycle mutators name <see cref="Roles.CreditLineManagers"/> — RiskManager
    /// included, Auditor excluded — rather than an inline role string.
    /// </summary>
    [Theory]
    [InlineData(nameof(CreditLinesController.RaiseCreditLineAsync))]
    [InlineData(nameof(CreditLinesController.SuspendCreditLineAsync))]
    [InlineData(nameof(CreditLinesController.ReactivateCreditLineAsync))]
    [InlineData(nameof(CreditLinesController.ConfirmOperationVigenciaAsync))]
    [InlineData(nameof(CreditLinesController.CancelOperationAsync))]
    public void CreditLineMutators_UseCreditLineManagers(string method)
    {
        Assert.Equal(
            Roles.CreditLineManagers,
            AuthorizationTestHelpers.DeclaredRolesOf(typeof(CreditLinesController), method));

        var roles = AuthorizationTestHelpers.AuthorizeRolesOf(typeof(CreditLinesController), method);
        Assert.Contains(Roles.RiskManager, roles);
        Assert.DoesNotContain(Roles.Auditor, roles);
    }

    // ── Scoring-config reads ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(SettingsController.GetScoringConfig))]
    [InlineData(nameof(SettingsController.GetSectors))]
    [InlineData(nameof(SettingsController.GetSectorProfile))]
    public void ScoringConfigReads_DeclareTenantConfigReaders(string method)
    {
        Assert.Equal(
            Roles.TenantConfigReaders,
            AuthorizationTestHelpers.DeclaredRolesOf(typeof(SettingsController), method));
    }

    // ── Removed endpoints ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("TestArcaAuthentication")]
    [InlineData("TestLookupArca")]
    public void AnonymousArcaTestActions_AreGone(string method)
    {
        Assert.False(AuthorizationTestHelpers.HasAction(typeof(EntitiesController), method));
    }

    [Theory]
    [InlineData("test-arca-auth")]
    [InlineData("lookup-arca/test")]
    public void AnonymousArcaTestRoutes_AreGone(string template)
    {
        Assert.DoesNotContain(template, RouteTemplatesOf(typeof(EntitiesController)));
    }

    [Fact]
    public void EntitiesController_HasNoAnonymousAction()
    {
        var anonymous = typeof(EntitiesController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any())
            .Select(m => m.Name);

        Assert.Empty(anonymous);
    }

    /// <summary>
    /// POST /api/bff/users/invite checked a "roles" claim the token never carries, so it always
    /// answered 403; the live invite is POST /api/bff/settings/users/invite.
    /// </summary>
    [Fact]
    public void LegacyInviteAction_IsGone()
    {
        Assert.False(AuthorizationTestHelpers.HasAction(typeof(InvitationController), "InviteUserAsync"));
        Assert.DoesNotContain("users/invite", RouteTemplatesOf(typeof(InvitationController)));
    }

    // ── Role constants ───────────────────────────────────────────────────────────

    [Fact]
    public void Operators_IsExactlyTheThreeOperatorRoles()
    {
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { Roles.Analyst, Roles.RiskManager, Roles.Admin },
            Split(Roles.Operators));
    }

    [Fact]
    public void CreditLineReaders_IsTheOperatorsPlusTheTwoReadOnlyRoles()
    {
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
                { Roles.Analyst, Roles.RiskManager, Roles.Admin, Roles.Auditor, Roles.SuperAdmin },
            Split(Roles.CreditLineReaders));
    }

    [Fact]
    public void Operators_ExcludesSuperAdmin()
    {
        Assert.DoesNotContain(Roles.SuperAdmin, Split(Roles.Operators));
    }

    // ── The role dashboards ──────────────────────────────────────────────────────

    public static TheoryData<string> DashboardActions => new()
    {
        nameof(DashboardController.GetMyWorkAsync),
        nameof(DashboardController.GetApprovalsAsync),
    };

    [Theory]
    [MemberData(nameof(DashboardActions))]
    public void DashboardAction_DeclaresDeciders(string method)
    {
        // The approvals action narrows further to confirmers off the DATABASE row, exactly as
        // PATCH .../decision does — so its outer gate is the decision endpoint's, Deciders.
        Assert.Equal(Roles.Deciders, AuthorizationTestHelpers.DeclaredRolesOf(typeof(DashboardController), method));
    }

    [Theory]
    [MemberData(nameof(DashboardActions))]
    public void DashboardAction_DeniesAuditorAndSuperAdmin(string method)
    {
        Assert.False(AuthorizationTestHelpers.RoleAllowed(typeof(DashboardController), method, Roles.Auditor));
        Assert.False(AuthorizationTestHelpers.RoleAllowed(typeof(DashboardController), method, Roles.SuperAdmin));
    }

    public static TheoryData<string> TenantWideDashboardActions => new()
    {
        nameof(DashboardController.GetPortfolioAsync),
        nameof(DashboardController.GetOversightAsync),
    };

    [Theory]
    [MemberData(nameof(TenantWideDashboardActions))]
    public void TenantWideDashboardAction_DeclaresTenantConfigReaders_AndAllowsTheReadOnlyRoles(string method)
    {
        Assert.Equal(Roles.TenantConfigReaders, AuthorizationTestHelpers.DeclaredRolesOf(typeof(DashboardController), method));
        Assert.True(AuthorizationTestHelpers.RoleAllowed(typeof(DashboardController), method, Roles.Admin));
        Assert.True(AuthorizationTestHelpers.RoleAllowed(typeof(DashboardController), method, Roles.RiskManager));
        Assert.True(AuthorizationTestHelpers.RoleAllowed(typeof(DashboardController), method, Roles.Auditor));
        Assert.True(AuthorizationTestHelpers.RoleAllowed(typeof(DashboardController), method, Roles.SuperAdmin));
    }

    [Fact]
    public void OperationsDashboard_IsAdminOnly_AndRefusesTheAuditorAndTheRiskManager()
    {
        const string method = nameof(DashboardController.GetOperationsAsync);

        Assert.Equal(Roles.Admin, AuthorizationTestHelpers.DeclaredRolesOf(typeof(DashboardController), method));
        Assert.True(AuthorizationTestHelpers.RoleAllowed(typeof(DashboardController), method, Roles.Admin));
        Assert.False(AuthorizationTestHelpers.RoleAllowed(typeof(DashboardController), method, Roles.Auditor));
        Assert.False(AuthorizationTestHelpers.RoleAllowed(typeof(DashboardController), method, Roles.RiskManager));
    }

    [Theory]
    [InlineData(nameof(DashboardController.GetPortfolioAsync))]
    [InlineData(nameof(DashboardController.GetOversightAsync))]
    [InlineData(nameof(DashboardController.GetOperationsAsync))]
    public void TenantWideDashboardActions_RefuseTheAnalyst(string method)
    {
        Assert.False(AuthorizationTestHelpers.RoleAllowed(typeof(DashboardController), method, Roles.Analyst));
    }

    /// <summary>The Admin operations dashboard refuses read-only support, as it refuses the Auditor.</summary>
    [Fact]
    public void OperationsDashboard_RefusesTheSuperAdmin()
    {
        Assert.False(AuthorizationTestHelpers.RoleAllowed(
            typeof(DashboardController), nameof(DashboardController.GetOperationsAsync), Roles.SuperAdmin));
    }

    [Fact]
    public void DashboardController_IsReadOnly_AndRequiresTheInternalScheme()
    {
        Assert.Empty(WriteActionsOf(typeof(DashboardController)));
        Assert.Contains(
            typeof(DashboardController).GetCustomAttributes<AuthorizeAttribute>(inherit: true),
            a => a.AuthenticationSchemes == "Internal");
    }

    private static IReadOnlyList<MethodInfo> WriteActionsOf(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                  .Where(m => m.GetCustomAttributes<HttpMethodAttribute>(inherit: true)
                               .Any(a => a.HttpMethods.Any(v => v is "POST" or "PUT" or "PATCH" or "DELETE")))
                  .ToList();

    private static HashSet<string> RouteTemplatesOf(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                  .SelectMany(m => m.GetCustomAttributes<HttpMethodAttribute>(inherit: true))
                  .Select(a => a.Template ?? string.Empty)
                  .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> Split(string roles) =>
        roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .ToHashSet(StringComparer.Ordinal);
}
