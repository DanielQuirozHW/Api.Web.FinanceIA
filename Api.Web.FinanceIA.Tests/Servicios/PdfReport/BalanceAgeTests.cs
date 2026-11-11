// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-09

using Api.Web.FinanceIA.Servicios.Pdf;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfReport;

/// <summary>QA Q49: "Vigencia de los datos" counts complete months from the last day of the closing month.</summary>
public sealed class BalanceAgeTests
{
    [Theory]
    [InlineData(2025, 11, 2026, 11, 8,  " (hace 11 meses)")]
    [InlineData(2025, 11, 2026, 11, 30, " (hace 12 meses)")]
    [InlineData(2025, 2,  2026, 2,  27, " (hace 11 meses)")]
    [InlineData(2025, 2,  2026, 2,  28, " (hace 12 meses)")]
    [InlineData(2026, 3,  2026, 4,  30, " (hace 1 mes)")]
    [InlineData(2026, 10, 2026, 11, 8,  " (hace menos de un mes)")]
    [InlineData(2026, 11, 2026, 11, 8,  " (cierre en el mes en curso)")]
    [InlineData(2026, 11, 2026, 11, 30, " (cierre en el mes en curso)")]
    [InlineData(2026, 12, 2026, 11, 8,  "")]
    public void Suffix(int year, int month, int nowYear, int nowMonth, int nowDay, string expected)
    {
        Assert.Equal(expected, BalanceAge.Suffix(year, month, new DateTime(nowYear, nowMonth, nowDay, 10, 0, 0)));
    }

    [Fact]
    public void AClosingOnThe31st_TurnsAMonthOnTheLastDayOfAShorterMonth()
    {
        // 31/01 → 28/02 (non-leap) is one complete month; 27/02 is not.
        Assert.Equal(1, BalanceAge.CompleteMonths(2026, 1, new DateTime(2026, 2, 28)));
        Assert.Equal(0, BalanceAge.CompleteMonths(2026, 1, new DateTime(2026, 2, 27)));
    }
}
