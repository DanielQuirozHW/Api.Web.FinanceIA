// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-03

using Api.Web.FinanceIA.Models.Lufe;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Models.Lufe;

/// <summary>
/// Covers <see cref="LufePeriodo.ParseYear"/> — the fiscal-year extractor used on Lufe
/// document import. Regression guard for the bug where a document's ejercicio was taken from
/// the entity-wide indicadores.periodo (latest statements) instead of the document's own
/// period: the extractor must read a 4-digit year from the document's periodo in its several
/// shapes, and return null (never a guess) when no plausible year is present.
/// </summary>
public sealed class LufePeriodoTests
{
    // Stand-in for DateTime.UtcNow.Year + 1 used by the caller. Fixed so the test is
    // deterministic and independent of the wall clock.
    private const int MaxYear = 2027;

    [Theory]
    [InlineData("2024",       2024)] // clean 4-digit
    [InlineData("202400",     2024)] // YYYY00 — the shape the old int.TryParse rejected outright
    [InlineData("202412",     2024)] // YYYYMM
    [InlineData("2024-12-31", 2024)] // date-like
    [InlineData("20241231",   2024)] // compact date → first run wins
    [InlineData("2023",       2023)]
    [InlineData("2027",       2027)] // upper bound (== maxYear) is inclusive
    [InlineData("1990",       1990)] // lower bound is inclusive
    public void ParseYear_ExtractsFirstPlausibleFourDigitRun(string periodo, int expected)
        => Assert.Equal(expected, LufePeriodo.ParseYear(periodo, MaxYear));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sin periodo")] // no digits at all
    [InlineData("12")]          // too short for a 4-digit run
    [InlineData("1899")]        // below the 1990 floor
    [InlineData("2099")]        // above maxYear
    [InlineData("999900")]      // 9999 out of range, no other run
    public void ParseYear_ReturnsNull_WhenNoPlausibleYear(string? periodo)
        => Assert.Null(LufePeriodo.ParseYear(periodo, MaxYear));
}
