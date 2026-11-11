// Project : Api.Web.FinanceIA.Tests
// Created : 2026-07-29

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Locks the scoring-threshold read/write role split on the BFF:
///   GET /api/bff/settings/scoring-config → Admin, RiskManager, Auditor, SuperAdmin (threshold:read)
///   PUT /api/bff/settings/scoring-config → Admin only                              (threshold:configure)
/// Fails if anyone widens the writer set or narrows the reader set.
/// </summary>
public sealed class ThresholdAuthorizationTests
{
    // ── Read thresholds: Admin, RiskManager, Auditor, read-only SuperAdmin allowed; Analyst denied ──
    [Theory]
    [InlineData(Roles.Admin,       true)]
    [InlineData(Roles.RiskManager, true)]
    [InlineData(Roles.Auditor,     true)]
    [InlineData(Roles.SuperAdmin,  true)]
    [InlineData(Roles.Analyst,     false)]
    public void ReadThresholds_AllowsExactlyAdminRiskManagerAuditorSuperAdmin(string role, bool allowed)
    {
        Assert.Equal(allowed, AuthorizationTestHelpers.RoleAllowed(
            typeof(SettingsController), nameof(SettingsController.GetScoringConfig), role));
    }

    // ── Edit thresholds: Admin only; RiskManager, Auditor and SuperAdmin must NOT write ──
    [Theory]
    [InlineData(Roles.Admin,       true)]
    [InlineData(Roles.RiskManager, false)]
    [InlineData(Roles.Auditor,     false)]
    [InlineData(Roles.SuperAdmin,  false)]
    [InlineData(Roles.Analyst,     false)]
    public void EditThresholds_AllowsExactlyAdmin(string role, bool allowed)
    {
        Assert.Equal(allowed, AuthorizationTestHelpers.RoleAllowed(
            typeof(SettingsController), nameof(SettingsController.UpdateScoringConfig), role));
    }

    // ── Exact-set guards: catch any added/removed role wholesale ──
    [Fact]
    public void ReadThresholds_RoleSet_IsExact()
    {
        var roles = AuthorizationTestHelpers.AuthorizeRolesOf(
            typeof(SettingsController), nameof(SettingsController.GetScoringConfig));
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { Roles.Admin, Roles.RiskManager, Roles.Auditor, Roles.SuperAdmin },
            roles);
    }

    [Fact]
    public void EditThresholds_RoleSet_IsExact()
    {
        var roles = AuthorizationTestHelpers.AuthorizeRolesOf(
            typeof(SettingsController), nameof(SettingsController.UpdateScoringConfig));
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { Roles.Admin },
            roles);
    }
}
