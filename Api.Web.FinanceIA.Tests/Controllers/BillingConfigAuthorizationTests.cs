// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-31

using System.Reflection;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Locks the role matrix around the billing endpoints, in the shape of
/// <see cref="ThresholdAuthorizationTests"/>: declared <c>[Authorize(Roles=...)]</c> metadata read
/// by reflection, no HTTP host.
///
/// <para>
/// Setting a tenant's commercial terms is the one thing in this API a client-portal Admin must not
/// be able to do, so "Admin is rejected" is the assertion that matters most here — an Admin is
/// otherwise the most privileged role in the product.
/// </para>
/// </summary>
public sealed class BillingConfigAuthorizationTests
{
    // ── Write config: SuperAdmin only ───────────────────────────────────────────

    [Theory]
    [InlineData(Roles.SuperAdmin,  true)]
    [InlineData(Roles.Admin,       false)]
    [InlineData(Roles.RiskManager, false)]
    [InlineData(Roles.Analyst,     false)]
    [InlineData(Roles.Auditor,     false)]
    public void UpdateConfig_AllowsExactlySuperAdmin(string role, bool allowed)
    {
        Assert.Equal(allowed, AuthorizationTestHelpers.RoleAllowed(
            typeof(BillingController), nameof(BillingController.UpdateConfigAsync), role));
    }

    /// <summary>Catches a role being added or removed wholesale, not just the five above.</summary>
    [Fact]
    public void UpdateConfig_RoleSet_IsExact()
    {
        var roles = AuthorizationTestHelpers.AuthorizeRolesOf(
            typeof(BillingController), nameof(BillingController.UpdateConfigAsync));

        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { Roles.SuperAdmin },
            roles);
    }

    // ── Read usage: unchanged by this delivery ──────────────────────────────────

    /// <summary>
    /// The quota banner is for the tenant's own users, so this endpoint declares no role gate and
    /// stays open to any authenticated principal. Asserted so the new write attribute cannot creep
    /// onto the read.
    /// </summary>
    [Fact]
    public void GetUsage_DeclaresNoRoleRestriction()
    {
        Assert.Empty(AuthorizationTestHelpers.AuthorizeRolesOf(
            typeof(BillingController), nameof(BillingController.GetUsageAsync)));
    }

    // ── SuperAdmin is not invitable from the client portal ──────────────────────

    /// <summary>
    /// A guard, not a restatement of the code: the invitation endpoint validates against
    /// <c>SettingsController.ValidRoles</c>, so adding "SuperAdmin" to that array would silently
    /// let a client-portal Admin invite one and grant themselves the billing write path. Seeding a
    /// SuperAdmin is a DBA operation.
    /// </summary>
    [Fact]
    public void SettingsControllerValidRoles_ExcludesSuperAdmin()
    {
        var field = typeof(SettingsController)
            .GetField("ValidRoles", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);

        var validRoles = Assert.IsType<string[]>(field.GetValue(null));

        Assert.DoesNotContain(Roles.SuperAdmin, validRoles);
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                Roles.Analyst, Roles.RiskManager, Roles.Admin, Roles.Auditor
            },
            validRoles.ToHashSet(StringComparer.Ordinal));
    }
}
