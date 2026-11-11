// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-31

using System.Reflection;
using Api.Web.FinanceIA.Clases.Filters;
using Api.Web.FinanceIA.Controllers;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Asserts by reflection that the billing quota gate is actually attached to the two endpoints it
/// is supposed to gate.
///
/// <para>
/// This is the other half of <c>BillingQuotaFilterTests</c>. Those prove the filter decides
/// correctly; these prove it is wired to anything at all. The pairing is necessary because every
/// controller test in this project invokes actions as plain methods, where filters never run — so
/// without this file, deleting the attribute would break enforcement in production while leaving
/// the whole suite green.
/// </para>
/// </summary>
public sealed class BillingQuotaAttributeTests
{
    private static MethodInfo Action(string name)
    {
        var method = typeof(AnalysisController).GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        return method!;
    }

    [Theory]
    [InlineData(nameof(AnalysisController.TriggerAsync), BillingQuotaKind.Analysis)]
    [InlineData(nameof(AnalysisController.ChatAsync), BillingQuotaKind.ChatTokens)]
    public void GatedActions_CarryTheBillingQuotaAttributeForTheRightLimit(string actionName, BillingQuotaKind expected)
    {
        var attribute = Action(actionName).GetCustomAttribute<BillingQuotaAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(typeof(BillingQuotaFilter), attribute!.ImplementationType);
        Assert.Equal(expected, Assert.Single(attribute.Arguments!));
    }

    /// <summary>
    /// The 429 has to be declared, or Swagger and the generated Portal client will not know the
    /// endpoint can refuse on quota.
    /// </summary>
    [Theory]
    [InlineData(nameof(AnalysisController.TriggerAsync))]
    [InlineData(nameof(AnalysisController.ChatAsync))]
    public void GatedActions_DeclareThe429Response(string actionName)
    {
        var declared = Action(actionName)
            .GetCustomAttributes<ProducesResponseTypeAttribute>()
            .Select(a => a.StatusCode);

        Assert.Contains(429, declared);
    }
}
