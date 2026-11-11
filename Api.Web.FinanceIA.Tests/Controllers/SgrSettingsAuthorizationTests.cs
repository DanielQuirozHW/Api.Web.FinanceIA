// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-25

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// GET and PUT /api/bff/settings/sgr are the tenant Admin's alone, read by reflection in the shape of
/// <see cref="TenantProfileAuthorizationTests"/>. The Fondo de Riesgo is the client tenant's own figure —
/// unlike the tenant profile, it commits nobody to vendor billing — so it is not SuperAdmin's.
/// </summary>
public sealed class SgrSettingsAuthorizationTests
{
    [Theory]
    [InlineData(nameof(SettingsController.GetSgrSettings))]
    [InlineData(nameof(SettingsController.UpdateSgrSettings))]
    public void SgrSettings_RoleSet_IsExactlyAdmin(string method)
    {
        var roles = AuthorizationTestHelpers.AuthorizeRolesOf(typeof(SettingsController), method);

        Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { Roles.Admin }, roles);
    }

    [Theory]
    [InlineData(Roles.Admin,       true)]
    [InlineData(Roles.RiskManager, false)]
    [InlineData(Roles.Auditor,     false)]
    [InlineData(Roles.Analyst,     false)]
    [InlineData(Roles.SuperAdmin,  false)]
    public void UpdateSgrSettings_AllowsOnlyAdmin(string role, bool allowed)
    {
        Assert.Equal(allowed, AuthorizationTestHelpers.RoleAllowed(
            typeof(SettingsController), nameof(SettingsController.UpdateSgrSettings), role));
        Assert.Equal(allowed, AuthorizationTestHelpers.RoleAllowed(
            typeof(SettingsController), nameof(SettingsController.GetSgrSettings), role));
    }
}
