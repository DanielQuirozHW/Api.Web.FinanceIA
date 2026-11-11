// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-09

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Locks the role matrix around the tenant-profile endpoints, in the shape of
/// <see cref="BillingConfigAuthorizationTests"/>: declared <c>[Authorize(Roles=...)]</c> metadata
/// read by reflection, no HTTP host.
///
/// <para>
/// Reading is deliberately broader than writing. A RiskManager and an Auditor must be able to SEE
/// which integrations the tenant is entitled to — it explains why an analysis carries the
/// enrichment it does — while only the cross-tenant SuperAdmin may move it: the entity-type switch
/// decides which vendors are called, so it commits the tenant to per-query vendor billing and is
/// therefore OUR configuration, not the client tenant's.
/// </para>
///
/// <para>
/// The two assertions worth having: <see cref="Roles.Admin"/> reads but no longer WRITES — a tenant
/// Admin losing a write it used to hold is the regression a careless widening would undo — and
/// <see cref="Roles.Analyst"/> is excluded from BOTH, an analyst being the plausible role to leak
/// into a read set by accident.
/// </para>
/// </summary>
public sealed class TenantProfileAuthorizationTests
{
    // ── Read: Admin, RiskManager, Auditor, SuperAdmin ───────────────────────────

    [Theory]
    [InlineData(Roles.Admin,       true)]
    [InlineData(Roles.RiskManager, true)]
    [InlineData(Roles.Auditor,     true)]
    [InlineData(Roles.SuperAdmin,  true)]
    [InlineData(Roles.Analyst,     false)]
    public void GetTenantProfile_AllowsTheConfigReaders(string role, bool allowed)
    {
        Assert.Equal(allowed, AuthorizationTestHelpers.RoleAllowed(
            typeof(SettingsController), nameof(SettingsController.GetTenantProfile), role));
    }

    /// <summary>Catches a role being added or removed wholesale, not just the five above.</summary>
    [Fact]
    public void GetTenantProfile_RoleSet_IsExact()
    {
        var roles = AuthorizationTestHelpers.AuthorizeRolesOf(
            typeof(SettingsController), nameof(SettingsController.GetTenantProfile));

        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                Roles.Admin, Roles.RiskManager, Roles.Auditor, Roles.SuperAdmin
            },
            roles);
    }

    // ── Write: SuperAdmin only ──────────────────────────────────────────────────

    [Theory]
    [InlineData(Roles.SuperAdmin,  true)]
    [InlineData(Roles.Admin,       false)]
    [InlineData(Roles.RiskManager, false)]
    [InlineData(Roles.Auditor,     false)]
    [InlineData(Roles.Analyst,     false)]
    public void UpdateTenantProfile_AllowsExactlySuperAdmin(string role, bool allowed)
    {
        Assert.Equal(allowed, AuthorizationTestHelpers.RoleAllowed(
            typeof(SettingsController), nameof(SettingsController.UpdateTenantProfile), role));
    }

    [Fact]
    public void UpdateTenantProfile_RoleSet_IsExact()
    {
        var roles = AuthorizationTestHelpers.AuthorizeRolesOf(
            typeof(SettingsController), nameof(SettingsController.UpdateTenantProfile));

        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { Roles.SuperAdmin },
            roles);
    }

    /// <summary>
    /// The write set must stay a strict subset of the read set: a role that may change the profile
    /// but not read it back would be a nonsense permission. This is what puts SuperAdmin in the read
    /// set — it is the writer, so it has to be able to read back what it set.
    /// </summary>
    [Fact]
    public void WriteRoles_AreASubsetOfReadRoles()
    {
        var readers = AuthorizationTestHelpers.AuthorizeRolesOf(
            typeof(SettingsController), nameof(SettingsController.GetTenantProfile));
        var writers = AuthorizationTestHelpers.AuthorizeRolesOf(
            typeof(SettingsController), nameof(SettingsController.UpdateTenantProfile));

        Assert.ProperSubset(readers, writers);
    }
}
