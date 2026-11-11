// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-04

using Api.Web.FinanceIA.Models.Financia;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Models;

/// <summary>
/// Pins the shape of <see cref="TenantProfilePresets"/>: which preset is the default, what each
/// preset says, and how <see cref="TenantProfilePresets.Find"/> and
/// <see cref="TenantProfilePresets.ForEntityType"/> treat unknown input.
/// </summary>
public sealed class TenantProfilePresetsTests
{
    [Fact]
    public void Default_IsTheSgrPreset()
    {
        Assert.Same(TenantProfilePresets.Sgr, TenantProfilePresets.Default);
        Assert.Equal(TenantSettingKeys.EntityTypeDefault, TenantProfilePresets.Default.EntityType);
    }

    [Fact]
    public void Sgr_HasEveryOptionalIntegrationOn()
    {
        var sgr = TenantProfilePresets.Sgr;

        Assert.Equal(TenantSettingKeys.EntityTypeSgr, sgr.EntityType);
        Assert.True(sgr.LufeEnabled);
        Assert.True(sgr.CasfogEnabled);
        Assert.True(sgr.NosisEnabled);
    }

    [Fact]
    public void NonSgr_DisablesCasfogAndNosis()
    {
        var nonSgr = TenantProfilePresets.NonSgr;

        Assert.Equal(TenantSettingKeys.EntityTypeNonSgr, nonSgr.EntityType);

        // LUFE is a public company/partner registry, useful to any lender.
        Assert.True(nonSgr.LufeEnabled);

        // CASFOG is the SGR guarantee-fund registry — meaningless to a non-SGR lender.
        Assert.False(nonSgr.CasfogEnabled);

        // NOSIS is billed per query, so the default that costs nothing is the one shipped. A tenant
        // that wants it turns it on with one integration.nosis.enabled row.
        Assert.False(nonSgr.NosisEnabled);
    }

    [Fact]
    public void All_ListsEveryPresetWithDistinctEntityTypes()
    {
        Assert.Equal(2, TenantProfilePresets.All.Count);
        Assert.Contains(TenantProfilePresets.Sgr, TenantProfilePresets.All);
        Assert.Contains(TenantProfilePresets.NonSgr, TenantProfilePresets.All);
        Assert.Equal(
            TenantProfilePresets.All.Count,
            TenantProfilePresets.All.Select(p => p.EntityType).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("sgr")]
    [InlineData("SGR")]
    [InlineData("  sgr  ")]
    public void Find_MatchesCaseInsensitivelyAndIgnoresWhitespace(string input)
    {
        Assert.Same(TenantProfilePresets.Sgr, TenantProfilePresets.Find(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bank")]
    [InlineData("non-sgr")]   // hyphen, not the underscore the key uses
    public void Find_ReturnsNullForUnknownInput(string? input)
    {
        Assert.Null(TenantProfilePresets.Find(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("bank")]
    public void ForEntityType_FallsBackToDefaultForUnknownInput(string? input)
    {
        Assert.Same(TenantProfilePresets.Default, TenantProfilePresets.ForEntityType(input));
    }

    [Fact]
    public void ForEntityType_ReturnsTheMatchingPreset()
    {
        Assert.Same(TenantProfilePresets.NonSgr, TenantProfilePresets.ForEntityType("non_sgr"));
    }
}
