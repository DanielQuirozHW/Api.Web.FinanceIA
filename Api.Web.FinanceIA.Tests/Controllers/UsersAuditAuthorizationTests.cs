// Project : Api.Web.FinanceIA.Tests
// Created : 2026-07-29

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Locks user-management and audit-log role enforcement on the BFF:
///   GET  /api/bff/settings/users        → Admin only
///   POST /api/bff/settings/users/invite → Admin only
///   GET  /api/bff/audit/logs            → Admin, Auditor, SuperAdmin (read-only support)
/// The front currently (wrongly) grants RiskManager access to audit; the BFF is the
/// source of truth and denies it. These tests keep the BFF correct while the portal
/// PR fixes the front separately.
/// </summary>
public sealed class UsersAuditAuthorizationTests
{
    // ── Manage users: Admin only, on both the list and the invite endpoints ──
    [Theory]
    [InlineData(Roles.Admin,       true)]
    [InlineData(Roles.Analyst,     false)]
    [InlineData(Roles.RiskManager, false)]
    [InlineData(Roles.Auditor,     false)]
    [InlineData(Roles.SuperAdmin,  false)]
    public void ListUsers_AllowsExactlyAdmin(string role, bool allowed)
    {
        Assert.Equal(allowed, AuthorizationTestHelpers.RoleAllowed(
            typeof(SettingsController), nameof(SettingsController.GetUsers), role));
    }

    [Theory]
    [InlineData(Roles.Admin,       true)]
    [InlineData(Roles.Analyst,     false)]
    [InlineData(Roles.RiskManager, false)]
    [InlineData(Roles.Auditor,     false)]
    [InlineData(Roles.SuperAdmin,  false)]
    public void InviteUser_AllowsExactlyAdmin(string role, bool allowed)
    {
        Assert.Equal(allowed, AuthorizationTestHelpers.RoleAllowed(
            typeof(SettingsController), nameof(SettingsController.InviteUser), role));
    }

    // ── View audit log: Admin, Auditor and read-only SuperAdmin; RiskManager and Analyst denied ──
    [Theory]
    [InlineData(Roles.Admin,       true)]
    [InlineData(Roles.Auditor,     true)]
    [InlineData(Roles.SuperAdmin,  true)]
    [InlineData(Roles.RiskManager, false)]  // RiskManager must NOT see audit
    [InlineData(Roles.Analyst,     false)]
    public void ViewAuditLog_AllowsExactlyAdminAuditorSuperAdmin(string role, bool allowed)
    {
        Assert.Equal(allowed, AuthorizationTestHelpers.RoleAllowed(
            typeof(AuditController), nameof(AuditController.GetLogs), role));
    }

    // ── Exact-set guards ──
    [Fact]
    public void ManageUsers_RoleSets_AreExactlyAdmin()
    {
        var expected = new HashSet<string>(StringComparer.Ordinal) { Roles.Admin };
        Assert.Equal(expected, AuthorizationTestHelpers.AuthorizeRolesOf(
            typeof(SettingsController), nameof(SettingsController.GetUsers)));
        Assert.Equal(expected, AuthorizationTestHelpers.AuthorizeRolesOf(
            typeof(SettingsController), nameof(SettingsController.InviteUser)));
    }

    [Fact]
    public void ViewAuditLog_RoleSet_IsExact()
    {
        var roles = AuthorizationTestHelpers.AuthorizeRolesOf(
            typeof(AuditController), nameof(AuditController.GetLogs));
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { Roles.Admin, Roles.Auditor, Roles.SuperAdmin },
            roles);
        Assert.Equal(roles, AuthorizationTestHelpers.AuthorizeRolesOf(
            typeof(AuditController), nameof(AuditController.GetFilters)));
        Assert.Equal(Roles.AuditViewers, AuthorizationTestHelpers.DeclaredRolesOf(
            typeof(AuditController), nameof(AuditController.GetLogs)));
    }
}
