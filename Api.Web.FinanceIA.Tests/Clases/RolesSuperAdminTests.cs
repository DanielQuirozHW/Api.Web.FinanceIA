// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-31

using Api.Web.FinanceIA.Clases;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Covers the <see cref="Roles.SuperAdmin"/> constant and where it sits among the role-group
/// combinations.
///
/// <para>
/// SuperAdmin is read-only support: it belongs in every READ combination the Auditor is in
/// (<see cref="Roles.All"/>, <see cref="Roles.CreditLineReaders"/>, <see cref="Roles.FiadorReaders"/>,
/// <see cref="Roles.TenantConfigReaders"/>, <see cref="Roles.AuditViewers"/>) and in NO write
/// combination. These are compile-time const concatenations, so a SuperAdmin slipped into a write
/// constant would not show up anywhere else — the write half is the one these tests guard hardest.
/// </para>
/// </summary>
public sealed class RolesSuperAdminTests
{
    /// <summary>The value mirrors the JWT role claim and is matched case-sensitively.</summary>
    [Fact]
    public void SuperAdmin_HasTheExactClaimValue()
    {
        Assert.Equal("SuperAdmin", Roles.SuperAdmin);
    }

    /// <summary>The four client roles plus read-only SuperAdmin; <c>All</c> gates reads/exports only.</summary>
    [Fact]
    public void All_EnumeratesTheFourClientRoles_PlusSuperAdmin()
    {
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                Roles.Analyst, Roles.RiskManager, Roles.Admin, Roles.Auditor, Roles.SuperAdmin
            },
            Split(Roles.All));
    }

    // Keyed by name: several constants share a value today (that is the file's "independent policies"
    // pattern), and InlineData would collapse them into duplicates.
    public static TheoryData<string, string> AuditorReadConstants => new()
    {
        { nameof(Roles.All),                             Roles.All },
        { nameof(Roles.CreditLineReaders),               Roles.CreditLineReaders },
        { nameof(Roles.FiadorReaders),                   Roles.FiadorReaders },
        { nameof(Roles.TenantConfigReaders),             Roles.TenantConfigReaders },
        { nameof(Roles.TenantConfigReadersWithOperator), Roles.TenantConfigReadersWithOperator },
        { nameof(Roles.AuditViewers),                    Roles.AuditViewers },
    };

    public static TheoryData<string, string> ConstantsWithoutSuperAdmin => new()
    {
        { nameof(Roles.AnalysisCreators),   Roles.AnalysisCreators },
        { nameof(Roles.Deciders),           Roles.Deciders },
        { nameof(Roles.CreditLineBuilders), Roles.CreditLineBuilders },
        { nameof(Roles.CreditLineManagers), Roles.CreditLineManagers },
        { nameof(Roles.FiadorManagers),     Roles.FiadorManagers },
        { nameof(Roles.Operators),          Roles.Operators },
        { nameof(Roles.TeamReaders),        Roles.TeamReaders },
        { nameof(Roles.AlertReadMarkers),   Roles.AlertReadMarkers },
    };

    [Theory]
    [MemberData(nameof(AuditorReadConstants))]
    public void ReadConstant_WithTheAuditor_AlsoIncludesSuperAdmin(string name, string roles)
    {
        var set = Split(roles);

        Assert.True(set.Contains(Roles.Auditor), $"Roles.{name} no longer includes the Auditor.");
        Assert.True(set.Contains(Roles.SuperAdmin), $"Roles.{name} includes the Auditor but not SuperAdmin.");
    }

    [Fact]
    public void AuditViewers_IsExactlyAdminAuditorSuperAdmin()
    {
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { Roles.Admin, Roles.Auditor, Roles.SuperAdmin },
            Split(Roles.AuditViewers));
    }

    [Theory]
    [MemberData(nameof(ConstantsWithoutSuperAdmin))]
    public void WriteOrNonAuditorConstant_ExcludesSuperAdmin(string name, string roles)
    {
        Assert.False(Split(roles).Contains(Roles.SuperAdmin), $"Roles.{name} includes SuperAdmin.");
    }

    /// <summary>Its two writes stay its own.</summary>
    [Fact]
    public void TenantConfigWriters_IsExactlySuperAdmin()
    {
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { Roles.SuperAdmin },
            Split(Roles.TenantConfigWriters));
    }

    /// <summary>Alert read state is tenant-wide: the four client roles mark it, SuperAdmin does not.</summary>
    [Fact]
    public void AlertReadMarkers_IsExactlyTheFourClientRoles()
    {
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                Roles.Analyst, Roles.RiskManager, Roles.Admin, Roles.Auditor
            },
            Split(Roles.AlertReadMarkers));
    }

    private static HashSet<string> Split(string roles) =>
        roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .ToHashSet(StringComparer.Ordinal);
}
