// Project : Api.Web.FinanceIA.Tests
// Created : 2026-06-16

using Api.Web.FinanceIA.Servicios;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

public sealed class ScoreScaleTests
{
    [Theory]
    [InlineData(13.8,  860)]  // La Tranquerita — round(13.8)=14, (100-14)*10=860
    [InlineData(14,    860)]  // already integer
    [InlineData(0,    1000)]  // no risk → max NOSIS
    [InlineData(100,     0)]  // max risk → min NOSIS
    [InlineData(-1,   1000)]  // clamp inferior
    [InlineData(101,     0)]  // clamp superior
    [InlineData(50,    500)]  // midpoint
    public void ToNosis_ReturnsExpected(double scoreMotor, int expected)
    {
        var result = ScoreScale.ToNosis((decimal)scoreMotor);
        Assert.Equal(expected, result);
    }
}
