// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-09

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// <see cref="BalanceBaseFigures.ReadNone"/>: a stored balance read none of the seven base figures only when
/// it exists and every one of them is absent; a zero is a figure that was read.
/// </summary>
public sealed class BalanceBaseFiguresTests
{
    private static readonly BalanceBaseFiguresProjection Empty = new() { HasBalance = true };

    [Fact]
    public void ABalanceWithNoneOfTheSeven_ReadNone()
        => Assert.True(BalanceBaseFigures.ReadNone(Empty));

    public static TheoryData<string, BalanceBaseFiguresProjection> OneFigure => new()
    {
        { "assets",              Empty with { Assets             = "1000" } },
        { "current_assets",      Empty with { CurrentAssets      = "1000" } },
        { "current_liabilities", Empty with { CurrentLiabilities = "1000" } },
        { "liabilities",         Empty with { Liabilities        = "1000" } },
        { "equity",              Empty with { Equity             = "-50.5" } },
        { "net_revenue",         Empty with { NetRevenue         = "1e3" } },
        { "net_profit",          Empty with { NetProfit          = "0" } },     // a read zero counts
    };

    [Theory]
    [MemberData(nameof(OneFigure))]
    public void AnySingleBaseFigure_MeansTheBalanceWasRead(string key, BalanceBaseFiguresProjection figures)
    {
        Assert.False(BalanceBaseFigures.ReadNone(figures), key);
    }

    [Fact]
    public void NoStoredBalance_OrNoResultRow_IsNotProofOfAnEmptyRead()
    {
        Assert.False(BalanceBaseFigures.ReadNone(new BalanceBaseFiguresProjection { HasBalance = false }));
        Assert.False(BalanceBaseFigures.ReadNone(null));
    }
}
