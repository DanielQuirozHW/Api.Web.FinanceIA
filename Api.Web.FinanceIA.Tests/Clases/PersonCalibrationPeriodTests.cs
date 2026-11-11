// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-30

using Api.Web.FinanceIA.Clases;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>The board's window: inclusive Argentine days as a half-open UTC window, and its es-AR validation.</summary>
public sealed class PersonCalibrationPeriodTests
{
    // 01:00 UTC on Oct 24 is still Oct 23 in Buenos Aires.
    private static readonly DateTime NowUtc = new(2026, 10, 24, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Resolve_BuildsAHalfOpenUtcWindow_FromArgentineMidnights()
    {
        var (period, field, error) = PersonCalibrationPeriod.Resolve("2026-10-01", "2026-10-23", NowUtc);

        Assert.Null(field);
        Assert.Null(error);
        Assert.Equal(new DateOnly(2026, 10, 1), period!.Value.From);
        Assert.Equal(new DateOnly(2026, 10, 23), period.Value.To);
        Assert.Equal(new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc), period.Value.StartUtc);
        Assert.Equal(new DateTime(2026, 10, 24, 3, 0, 0, DateTimeKind.Utc), period.Value.EndUtc);
        Assert.Equal(DateTimeKind.Utc, period.Value.StartUtc.Kind);
    }

    [Fact]
    public void Resolve_Defaults_ToTheLast30ArgentineDays()
    {
        var period = PersonCalibrationPeriod.Resolve(null, " ", NowUtc).Period!.Value;

        Assert.Equal(new DateOnly(2026, 10, 23), period.To);
        Assert.Equal(new DateOnly(2026, 9, 24), period.From);
    }

    [Fact]
    public void Resolve_AcceptsExactly366Days_AndASingleDay()
    {
        Assert.NotNull(PersonCalibrationPeriod.Resolve("2025-10-24", "2026-10-24", NowUtc).Period);
        Assert.NotNull(PersonCalibrationPeriod.Resolve("2026-10-23", "2026-10-23", NowUtc).Period);
    }

    [Theory]
    [InlineData("2026-10-24", "2026-10-23", "from", "La fecha 'from' no puede ser posterior a 'to'.")]
    [InlineData("2025-10-23", "2026-10-24", "to", "El período no puede superar los 366 días.")]
    [InlineData("23/10/2026", "2026-10-24", "from", "La fecha 'from' debe tener el formato AAAA-MM-DD.")]
    [InlineData("2026-10-01", "2026-13-01", "to", "La fecha 'to' debe tener el formato AAAA-MM-DD.")]
    public void Resolve_RefusesWithAnEsArMessage(string from, string to, string expectedField, string expectedError)
    {
        var (period, field, error) = PersonCalibrationPeriod.Resolve(from, to, NowUtc);

        Assert.Null(period);
        Assert.Equal(expectedField, field);
        Assert.Equal(expectedError, error);
    }
}
