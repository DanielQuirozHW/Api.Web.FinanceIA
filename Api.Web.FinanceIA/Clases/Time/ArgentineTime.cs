// Project : Api.Web.FinanceIA
// Created : 2026-09-01

namespace Api.Web.FinanceIA.Clases.Time;

/// <summary>
/// A billing/usage period: one Argentine calendar month, expressed as a half-open UTC window.
/// </summary>
/// <param name="StartUtc">
/// 00:00 AR on day 1 of the month, converted to UTC. <see cref="DateTimeKind.Utc"/>, INCLUSIVE.
/// </param>
/// <param name="EndUtc">
/// 00:00 AR on day 1 of the NEXT month, converted to UTC. <see cref="DateTimeKind.Utc"/>, EXCLUSIVE.
/// </param>
/// <param name="Year">The ARGENTINE-local year the window covers — not the UTC one.</param>
/// <param name="Month">The ARGENTINE-local month the window covers — not the UTC one.</param>
/// <remarks>
/// The window is half-open so consecutive periods tile with no gap and no overlap: an event at the
/// exact boundary instant belongs to the later month and to that month only.
/// </remarks>
public readonly record struct BillingPeriod(DateTime StartUtc, DateTime EndUtc, int Year, int Month);

/// <summary>
/// The single source of truth for "what Argentine month is it, and which UTC instants bound it".
///
/// <para>
/// Why this exists: the business rule is that a usage period is the ARGENTINE calendar month, but
/// every timestamp we filter on — <c>Analyses.CreatedAt</c>, <c>ApiCallLogs.CalledAt</c>,
/// <c>ChatMessages.CreatedAt</c> — is stored in UTC. Building the window from
/// <c>DateTime.UtcNow.Year/Month</c> rolls the period over at 21:00 AR on the last day of the month,
/// resetting a tenant's consumption counter three hours early. So the boundary is computed in AR
/// local time and converted TO UTC before it ever reaches a query. Stored values are never touched.
/// </para>
///
/// <para>
/// Every call site derives its window from here. Before this type, five places each built
/// <c>new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc)</c> inline and a code comment was all
/// that kept the analysis count and the token count describing the same period.
/// </para>
/// </summary>
public static class ArgentineTime
{
    // Argentina has observed no DST since 2009, so the zone is a flat UTC-03:00 in practice. The
    // conversion still goes through TimeZoneInfo rather than arithmetic on a -3 constant: if the
    // rule ever changes, correct code should follow the tz database rather than need an edit.
    private const string IanaId = "America/Argentina/Buenos_Aires";
    private const string WindowsId = "Argentina Standard Time";

    /// <summary>
    /// The Argentine time zone, resolved once for the life of the process.
    /// </summary>
    /// <remarks>
    /// Resolved defensively, the same chain <c>PdfExportService</c> has used since it shipped.
    /// Production is a WINDOWS App Service (IIS in-process — see the rationale in
    /// <c>.github/workflows/deploy.yml</c>) while CI runs on ubuntu, so both the IANA id and the
    /// Windows id have to be tried. .NET 8 maps between them via ICU and the IANA id alone would
    /// very probably work on both, but "very probably" is not a basis for the code that decides what
    /// a customer is billed. The last resort is a fixed-offset zone rather than bare arithmetic, so
    /// that every caller below converts through one uniform code path.
    /// </remarks>
    public static TimeZoneInfo Zone { get; } = ResolveZone();

    /// <summary>
    /// Projects a UTC instant into Argentine local wall-clock time.
    /// </summary>
    public static DateTime ToLocal(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc), Zone);

    /// <summary>
    /// The billing period containing <paramref name="utcNow"/> — the Argentine month that instant
    /// falls in, as a half-open UTC window.
    /// </summary>
    /// <remarks>
    /// Takes the instant as a parameter rather than reading the clock so that one "now" can drive a
    /// whole snapshot. A caller that read the clock per query could straddle a boundary and meter two
    /// different periods in a single response.
    /// </remarks>
    public static BillingPeriod CurrentPeriod(DateTime utcNow)
    {
        var local = ToLocal(utcNow);
        return PeriodFor(local.Year, local.Month);
    }

    /// <summary>
    /// The billing period for an explicit ARGENTINE-local year and month.
    /// </summary>
    /// <param name="year">Argentine-local year.</param>
    /// <param name="month">Argentine-local month, 1-12.</param>
    /// <exception cref="ArgumentOutOfRangeException">The year/month pair is not a real month.</exception>
    public static BillingPeriod PeriodFor(int year, int month)
    {
        // Unspecified, NOT Utc: this is a local wall-clock reading that has not been placed on the
        // timeline yet. Marking it Utc here would make ConvertTimeToUtc below a no-op and quietly
        // reintroduce the very bug this type exists to fix.
        var localStart = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var localEnd = localStart.AddMonths(1);

        return new BillingPeriod(
            StartUtc: ToUtc(localStart),
            EndUtc: ToUtc(localEnd),
            Year: year,
            Month: month);
    }

    private static TimeZoneInfo ResolveZone()
    {
        foreach (string id in new[] { IanaId, WindowsId })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        return TimeZoneInfo.CreateCustomTimeZone(
            id: IanaId,
            baseUtcOffset: TimeSpan.FromHours(-3),
            displayName: "(UTC-03:00) Buenos Aires",
            standardDisplayName: "ART");
    }

    // ConvertTimeToUtc already returns Kind=Utc; midnight on the 1st is never an invalid or
    // ambiguous local time under a zone with no DST transitions, so no skip/overlap handling.
    private static DateTime ToUtc(DateTime local) => TimeZoneInfo.ConvertTimeToUtc(local, Zone);

    // Mirrors Clases/Json/UtcDateTimeConverter: a value read back from SQL datetime2 arrives as
    // Unspecified and is UTC by convention, so relabel it rather than reinterpreting it as local.
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
