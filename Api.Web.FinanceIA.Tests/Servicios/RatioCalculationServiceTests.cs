// Project : Api.Web.FinanceIA.Tests
// Created : 2026-06-10

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

// ── Test class ────────────────────────────────────────────────────────────────

public sealed class RatioCalculationServiceTests
{
    private readonly RatioCalculationService _sut = new();

    // ── Fixture helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Fixture A — healthy company.
    /// current=2.67, acid=1.92, debt=0.34, leverage=0.51,
    /// coverage=6.29, roe=27.36, roa=18.13, margin=10.00
    /// </summary>
    private static BalanceAccounts FixtureA() => new()
    {
        CurrentAssets        = 48_000_000,
        NonCurrentAssets     = 32_000_000,
        TotalAssets          = 80_000_000,
        CurrentLiabilities   = 18_000_000,
        NonCurrentLiabilities = 9_000_000,
        TotalLiabilities     = 27_000_000,
        Equity               = 53_000_000,
        NetRevenue           = 145_000_000,
        NetProfit            = 14_500_000,
        Inventory            = 12_000_000,
        PrepaidExpenses      = 1_500_000,
        OperatingResult      = 22_000_000,
        FinancialExpenses    = 3_500_000,
    };

    /// <summary>
    /// Fixture B — critical/distressed company.
    /// current=0.48, acid=0.18, debt=0.95, leverage=18.28,
    /// coverage=0.62, roe=-75.86, roa=-3.94, margin=-2.50
    /// </summary>
    private static BalanceAccounts FixtureB() => new()
    {
        CurrentAssets        = 14_900_000,
        NonCurrentAssets     = 41_000_000,
        TotalAssets          = 55_900_000,
        CurrentLiabilities   = 31_000_000,
        NonCurrentLiabilities = 22_000_000,
        TotalLiabilities     = 53_000_000,
        Equity               = 2_900_000,
        NetRevenue           = 88_000_000,
        NetProfit            = -2_200_000,
        Inventory            = 8_500_000,
        PrepaidExpenses      = 900_000,
        OperatingResult      = 4_200_000,
        FinancialExpenses    = 6_800_000,
    };

    // ── Happy-path fixtures ───────────────────────────────────────────────────

    [Fact]
    public void Calculate_HealthyFixture_ReturnsExpectedRatios()
    {
        var result = _sut.Calculate(FixtureA());

        Assert.Equal(2.67,  result.CurrentRatio);
        Assert.Equal(1.92,  result.AcidTest);
        Assert.Equal(0.34,  result.DebtRatio);
        Assert.Equal(0.51,  result.Leverage);
        Assert.Equal(6.29,  result.InterestCoverage);
        Assert.Equal(27.36, result.Roe);
        Assert.Equal(18.13, result.Roa);
        Assert.Equal(10.00, result.NetMargin);
    }

    [Fact]
    public void Calculate_CriticalFixture_ReturnsExpectedRatios()
    {
        var result = _sut.Calculate(FixtureB());

        Assert.Equal(0.48,   result.CurrentRatio);
        Assert.Equal(0.18,   result.AcidTest);
        Assert.Equal(0.95,   result.DebtRatio);
        Assert.Equal(18.28,  result.Leverage);
        Assert.Equal(0.62,   result.InterestCoverage);
        Assert.Equal(-75.86, result.Roe);
        Assert.Equal(-3.94,  result.Roa);
        Assert.Equal(-2.50,  result.NetMargin);
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public void Calculate_ZeroCurrentLiabilities_CurrentAndAcidNull()
    {
        var accounts = FixtureA() with { CurrentLiabilities = 0 };

        var result = _sut.Calculate(accounts);

        Assert.Null(result.CurrentRatio);
        Assert.Null(result.AcidTest);
    }

    [Fact]
    public void Calculate_NegativeEquity_LeverageAndRoeNull()
    {
        var accounts = FixtureA() with { Equity = -1_000_000 };

        var result = _sut.Calculate(accounts);

        Assert.Null(result.Leverage);
        Assert.Null(result.Roe);
    }

    [Fact]
    public void Calculate_NullInventory_AcidTestNull()
    {
        var accounts = FixtureA() with { Inventory = null };

        var result = _sut.Calculate(accounts);

        Assert.Null(result.AcidTest);
    }

    [Fact]
    public void Calculate_ZeroInventory_AcidTestEqualsCurrentRatio()
    {
        var accounts = new BalanceAccounts
        {
            CurrentAssets         = 18_000_000,
            NonCurrentAssets      = 20_000_000,
            TotalAssets           = 38_000_000,
            CurrentLiabilities    = 11_000_000,
            NonCurrentLiabilities = 8_000_000,
            TotalLiabilities      = 19_000_000,
            Equity                = 19_000_000,
            Inventory             = 0,
        };

        var result = _sut.Calculate(accounts);

        Assert.Equal(result.CurrentRatio, result.AcidTest);
        Assert.Equal(1.64, result.CurrentRatio);   // 18M / 11M = 1.636… → 1.64
    }

    [Fact]
    public void Calculate_ZeroInventory_Chacabuco_AcidTestMatchesCurrentRatio()
    {
        // Chacabuco Noroeste Tour S.R.L. — ejercicio 2025 — sin bienes de cambio
        var accounts = new BalanceAccounts
        {
            CurrentAssets       = 442_300_939.35,
            CurrentLiabilities  = 515_219_298.36,
            TotalAssets         = 442_300_939.35,
            TotalLiabilities    = 515_219_298.36,
            Inventory           = 0,
        };

        var result = _sut.Calculate(accounts);

        // acid_test = (442_300_939.35 − 0) / 515_219_298.36 = 0.8585 → 0.86
        Assert.Equal(0.86, result.AcidTest);
        Assert.Equal(result.CurrentRatio, result.AcidTest);
    }

    [Fact]
    public void Calculate_NegativeInventory_AcidTestNull()
    {
        var accounts = new BalanceAccounts
        {
            CurrentAssets         = 18_000_000,
            NonCurrentAssets      = 20_000_000,
            TotalAssets           = 38_000_000,
            CurrentLiabilities    = 11_000_000,
            NonCurrentLiabilities = 8_000_000,
            TotalLiabilities      = 19_000_000,
            Equity                = 19_000_000,
            Inventory             = -1,
        };

        var result = _sut.Calculate(accounts);

        Assert.Null(result.AcidTest);
    }

    [Fact]
    public void Calculate_NullPrepaidExpenses_AcidComputedWithZero()
    {
        // PrepaidExpenses=null → treated as 0, acid_test must still be computed.
        var accounts = FixtureA() with { PrepaidExpenses = null };

        var result = _sut.Calculate(accounts);

        Assert.NotNull(result.AcidTest);
    }

    [Fact]
    public void Calculate_ZeroFinancialExpenses_CoverageNull()
    {
        var accounts = FixtureA() with { FinancialExpenses = 0 };

        var result = _sut.Calculate(accounts);

        Assert.Null(result.InterestCoverage);
    }

    [Fact]
    public void Calculate_AllNullInput_AllRatiosNull()
    {
        var result = _sut.Calculate(new BalanceAccounts());

        Assert.Null(result.CurrentRatio);
        Assert.Null(result.AcidTest);
        Assert.Null(result.DebtRatio);
        Assert.Null(result.Leverage);
        Assert.Null(result.InterestCoverage);
        Assert.Null(result.Roe);
        Assert.Null(result.Roa);
        Assert.Null(result.NetMargin);
    }

    [Fact]
    public void Calculate_NegativeFinancialExpenses_SameCoverageAsPositive()
    {
        // FinancialExpenses stored as negative (expense sign convention).
        // Math.Abs() must be applied → same coverage as positive 6_800_000.
        var accounts = FixtureB() with { FinancialExpenses = -6_800_000 };

        var result = _sut.Calculate(accounts);

        Assert.Equal(0.62, result.InterestCoverage);
    }
}
