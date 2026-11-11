// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-01

using Api.Web.FinanceIA.Clases.Time;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases.Time;

/// <summary>
/// Covers <see cref="ArgentineTime"/>, the single place that decides where a billing period begins
/// and ends.
///
/// <para>
/// Why this exists: the bug it fixes is invisible for 29 days a month. Billing used to build its
/// window from <c>DateTime.UtcNow.Year/Month</c>, so the period rolled over at 00:00 UTC on the 1st
/// — 21:00 ART on the last day of the previous month. For those three hours a tenant's consumption
/// counter had already reset and the portal showed a period the user did not consider started. Only
/// a test that names the exact instant can catch that; a test written at midday would pass against
/// either implementation.
/// </para>
///
/// <para>
/// The three-hour window is therefore the point of most of these cases. They are written in pairs:
/// an instant just before the ART boundary and one just after, both inside the SAME UTC day, which
/// is precisely the pair the old code got wrong.
/// </para>
///
/// <para>
/// Assertions pin 03:00 UTC because Argentina has observed no DST since 2009, so UTC-03:00 holds
/// year round. The production code still converts through <see cref="TimeZoneInfo"/> rather than
/// subtracting three hours — if the rule ever changes, these expectations are what should be
/// updated, deliberately, rather than silently drifting.
/// </para>
/// </summary>
public sealed class ArgentineTimeTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0)
        => new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    /// <summary>00:00 ART on day 1 of the given month, expressed as the UTC instant it maps to.</summary>
    private static DateTime ArMonthStartAsUtc(int year, int month) => Utc(year, month, 1, 3);

    // ── The three hours the old code got wrong ──────────────────────────────────

    /// <summary>
    /// 23:30 on 31 August in Buenos Aires. It is already 1 September in UTC, and the previous
    /// implementation metered this against September. It is still August for the tenant.
    /// </summary>
    [Fact]
    public void CurrentPeriod_InTheLastHoursOfAnArgentineMonth_StaysInThatMonth()
    {
        var period = ArgentineTime.CurrentPeriod(Utc(2026, 9, 1, 2, 30));

        Assert.Equal(2026, period.Year);
        Assert.Equal(8, period.Month);
        Assert.Equal(ArMonthStartAsUtc(2026, 8), period.StartUtc);
        Assert.Equal(ArMonthStartAsUtc(2026, 9), period.EndUtc);
    }

    /// <summary>
    /// 00:30 on 1 September in Buenos Aires — thirty minutes later, and the same UTC day as the
    /// case above. This one really is the new month.
    /// </summary>
    [Fact]
    public void CurrentPeriod_InTheFirstMinutesOfAnArgentineMonth_IsTheNewMonth()
    {
        var period = ArgentineTime.CurrentPeriod(Utc(2026, 9, 1, 3, 30));

        Assert.Equal(2026, period.Year);
        Assert.Equal(9, period.Month);
        Assert.Equal(ArMonthStartAsUtc(2026, 9), period.StartUtc);
        Assert.Equal(ArMonthStartAsUtc(2026, 10), period.EndUtc);
    }

    /// <summary>
    /// The boundary instant itself belongs to the later month — the window is half-open at the
    /// start, so 00:00:00 ART on the 1st opens the new period rather than closing the old one.
    /// </summary>
    [Fact]
    public void CurrentPeriod_AtTheExactBoundaryInstant_OpensTheNewMonth()
    {
        var period = ArgentineTime.CurrentPeriod(ArMonthStartAsUtc(2026, 9));

        Assert.Equal(9, period.Month);
        Assert.Equal(ArMonthStartAsUtc(2026, 9), period.StartUtc);
    }

    /// <summary>One tick before it is still the old month — the other side of the same edge.</summary>
    [Fact]
    public void CurrentPeriod_OneTickBeforeTheBoundary_IsStillTheOldMonth()
    {
        var period = ArgentineTime.CurrentPeriod(ArMonthStartAsUtc(2026, 9).AddTicks(-1));

        Assert.Equal(8, period.Month);
        Assert.Equal(ArMonthStartAsUtc(2026, 9), period.EndUtc);
    }

    // ── Year rollover ───────────────────────────────────────────────────────────

    /// <summary>
    /// 23:30 on 31 December in Buenos Aires is already 2027 in UTC. Getting the YEAR wrong here
    /// would meter a tenant against a month it has no rows in and report every quota at zero.
    /// </summary>
    [Fact]
    public void CurrentPeriod_OnArgentineNewYearsEve_StaysInTheOldYear()
    {
        var period = ArgentineTime.CurrentPeriod(Utc(2027, 1, 1, 2, 30));

        Assert.Equal(2026, period.Year);
        Assert.Equal(12, period.Month);
        Assert.Equal(ArMonthStartAsUtc(2026, 12), period.StartUtc);
        Assert.Equal(ArMonthStartAsUtc(2027, 1), period.EndUtc);
    }

    /// <summary>December must roll into January of the FOLLOWING year, not into month 13.</summary>
    [Fact]
    public void PeriodFor_December_RollsOverIntoTheNextYear()
    {
        var period = ArgentineTime.PeriodFor(2026, 12);

        Assert.Equal(ArMonthStartAsUtc(2027, 1), period.EndUtc);
    }

    // ── Shape of the window ─────────────────────────────────────────────────────

    /// <summary>
    /// Both boundaries are UTC instants. They are handed straight to EF as query parameters against
    /// UTC columns, so a Local or Unspecified Kind leaking out of here would be compared against
    /// the wrong timeline.
    /// </summary>
    [Theory]
    [InlineData(2026, 1)]
    [InlineData(2026, 6)]
    [InlineData(2026, 12)]
    public void PeriodFor_ProducesUtcBoundaries(int year, int month)
    {
        var period = ArgentineTime.PeriodFor(year, month);

        Assert.Equal(DateTimeKind.Utc, period.StartUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, period.EndUtc.Kind);
    }

    /// <summary>
    /// Every boundary is 00:00 ART on the 1st, which under the current UTC-03:00 rule is 03:00 UTC
    /// on the 1st — never midnight UTC, which is what the bug looked like.
    /// </summary>
    [Theory]
    [InlineData(2026, 1)]
    [InlineData(2026, 3)]
    [InlineData(2026, 7)]
    [InlineData(2026, 10)]
    [InlineData(2026, 12)]
    public void PeriodFor_BoundaryIsMidnightArgentineTime_WhichIsThreeAmUtcOnTheFirst(int year, int month)
    {
        var period = ArgentineTime.PeriodFor(year, month);

        Assert.Equal(ArMonthStartAsUtc(year, month), period.StartUtc);
        Assert.Equal(1, period.StartUtc.Day);
        Assert.Equal(3, period.StartUtc.Hour);
        Assert.Equal(0, period.StartUtc.Minute);

        // And it really is midnight once projected back into Buenos Aires.
        var localStart = ArgentineTime.ToLocal(period.StartUtc);
        Assert.Equal(new DateTime(year, month, 1, 0, 0, 0), localStart);
    }

    /// <summary>
    /// Consecutive periods tile: no instant falls in both, and none falls in neither. This is what
    /// makes the half-open window safe to bill against.
    /// </summary>
    [Theory]
    [InlineData(2026, 1)]
    [InlineData(2026, 11)]
    [InlineData(2026, 12)]
    public void PeriodFor_EndOfOneMonth_IsExactlyTheStartOfTheNext(int year, int month)
    {
        var period = ArgentineTime.PeriodFor(year, month);
        var next = month == 12
            ? ArgentineTime.PeriodFor(year + 1, 1)
            : ArgentineTime.PeriodFor(year, month + 1);

        Assert.Equal(next.StartUtc, period.EndUtc);
    }

    /// <summary>
    /// A month is always longer than 27 days and shorter than 32 — a guard that would catch a
    /// conversion collapsing the window or overshooting by a month.
    /// </summary>
    [Theory]
    [InlineData(2026, 2, 28)]
    [InlineData(2028, 2, 29)]   // leap year
    [InlineData(2026, 4, 30)]
    [InlineData(2026, 7, 31)]
    public void PeriodFor_SpansTheRealLengthOfTheMonth(int year, int month, int expectedDays)
    {
        var period = ArgentineTime.PeriodFor(year, month);

        Assert.Equal(expectedDays, (period.EndUtc - period.StartUtc).TotalDays);
    }

    /// <summary>The instant used to derive a period always lies inside it.</summary>
    [Theory]
    [InlineData(2026, 1, 1, 3)]      // first instant of the AR year
    [InlineData(2026, 6, 15, 12)]
    [InlineData(2026, 9, 1, 2)]      // the 21:00-24:00 ART band
    [InlineData(2027, 1, 1, 2)]      // AR new year's eve
    public void CurrentPeriod_AlwaysContainsTheInstantItWasDerivedFrom(int y, int m, int d, int h)
    {
        var instant = Utc(y, m, d, h);

        var period = ArgentineTime.CurrentPeriod(instant);

        Assert.True(period.StartUtc <= instant, "period starts at or before the instant");
        Assert.True(instant < period.EndUtc, "period ends strictly after the instant");
    }

    // ── Zone resolution ─────────────────────────────────────────────────────────

    /// <summary>
    /// The zone must resolve to a real UTC-03:00, whichever of the three resolution paths the host
    /// took. CI is Linux (the IANA id) and production is a Windows App Service (the Windows id);
    /// this asserts the observable result rather than which branch ran.
    /// </summary>
    [Fact]
    public void Zone_ResolvesToUtcMinusThree()
    {
        var offset = ArgentineTime.Zone.GetUtcOffset(new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(TimeSpan.FromHours(-3), offset);
    }

    /// <summary>
    /// Argentina has observed no DST since 2009, so the offset must not move across the year. A
    /// host whose tz database disagreed would shift a billing boundary by an hour twice a year.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(10)]
    public void Zone_HasNoDaylightSavingShift(int month)
    {
        var offset = ArgentineTime.Zone.GetUtcOffset(new DateTime(2026, month, 15, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(TimeSpan.FromHours(-3), offset);
    }

    // ── ToLocal ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ToLocal_SubtractsThreeHours()
    {
        Assert.Equal(new DateTime(2026, 8, 31, 23, 30, 0), ArgentineTime.ToLocal(Utc(2026, 9, 1, 2, 30)));
    }

    /// <summary>
    /// A value read back from SQL <c>datetime2</c> arrives with <see cref="DateTimeKind.Unspecified"/>
    /// but is UTC by this codebase's convention, and UsageRepository projects exactly such values to
    /// build its per-day buckets. Treating one as local time would shift a whole day's bucket.
    /// </summary>
    [Fact]
    public void ToLocal_TreatsAnUnspecifiedKindAsUtc()
    {
        var fromDatabase = new DateTime(2026, 9, 1, 2, 30, 0, DateTimeKind.Unspecified);

        Assert.Equal(ArgentineTime.ToLocal(Utc(2026, 9, 1, 2, 30)), ArgentineTime.ToLocal(fromDatabase));
    }

    // ── Guard rails ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public void PeriodFor_WithAMonthOutsideOneToTwelve_Throws(int month)
    {
        // Block-bodied so the lambda binds to Assert.Throws(Action): PeriodFor returns a struct,
        // and an expression lambda would also be convertible to the Func<object> overload.
        Assert.Throws<ArgumentOutOfRangeException>(() => { ArgentineTime.PeriodFor(2026, month); });
    }
}
