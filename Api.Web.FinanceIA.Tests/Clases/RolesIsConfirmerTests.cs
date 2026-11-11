// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-28

using System.Reflection;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Pins the in-code role comparisons a read-only role must never pass: <see cref="Roles.IsConfirmer"/>
/// (who may sign a pre-approval and who counts toward the quorum) and
/// <c>CreditLinesController.CanMutate</c> (who may manage a cupo they did not create). Both are
/// RiskManager + Admin only; the Auditor and the read-only SuperAdmin get false, exactly alike.
/// </summary>
public sealed class RolesIsConfirmerTests
{
    [Theory]
    [InlineData(Roles.RiskManager, true)]
    [InlineData(Roles.Admin,       true)]
    [InlineData(Roles.Analyst,     false)]
    [InlineData(Roles.Auditor,     false)]
    [InlineData(Roles.SuperAdmin,  false)]
    [InlineData("admin",           false)]   // ordinal: a case variant is not the role
    [InlineData("",                false)]
    [InlineData(null,              false)]
    public void IsConfirmer_IsRiskManagerAndAdminOnly(string? role, bool expected)
    {
        Assert.Equal(expected, Roles.IsConfirmer(role));
    }

    [Theory]
    [InlineData(Roles.RiskManager, true)]
    [InlineData(Roles.Admin,       true)]
    [InlineData(Roles.Analyst,     false)]
    [InlineData(Roles.Auditor,     false)]
    [InlineData(Roles.SuperAdmin,  false)]
    public void CanMutate_SomeoneElsesCupo_IsTheTenantWideRolesOnly(string role, bool expected)
    {
        Assert.Equal(expected, CanMutate(createdByUserId: Guid.NewGuid(), currentUserId: Guid.NewGuid(), role));
    }

    [Fact]
    public void CanMutate_OwnCupo_IsAllowedWhateverTheRole()
    {
        var userId = Guid.NewGuid();

        Assert.True(CanMutate(userId, userId, Roles.Analyst));
    }

    private static bool CanMutate(Guid createdByUserId, Guid currentUserId, string role)
    {
        var method = typeof(CreditLinesController).GetMethod(
            "CanMutate", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("CreditLinesController.CanMutate not found.");

        return (bool)method.Invoke(null, [createdByUserId, currentUserId, role])!;
    }
}
