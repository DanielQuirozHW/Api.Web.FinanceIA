// Project : Api.Web.FinanceIA
// Created : 2026-11-02

namespace Api.Web.FinanceIA.Servicios.ScoreReliability;

/// <summary>
/// The thresholds of the score-reliability verdict (score credibility 1b-2b), and its month arithmetic.
/// The ONE place they live: no config key, no DDL. Changing any of them changes verdicts, so bump
/// <see cref="PolicyVersion"/> with it.
/// <para>
/// Every age is measured in whole UTC CALENDAR months between the month of the fact and the month of the
/// score date (<c>RescoredAt ?? GeneratedAt</c>) — never against today, so a verdict does not drift as time
/// passes, and never day-sensitive, so no timezone or day-of-month edge can move it. "Exactly at" a
/// threshold and "one month over" are therefore whole calendar months.
/// </para>
/// </summary>
internal static class ScoreReliabilityPolicy
{
    /// <summary>
    /// The version of these rules, carried on every verdict. "2" (Phase 10-F3a) added the
    /// <c>manual_correction</c> caveat. "3" (score credibility 1b-5) added the provisional
    /// <c>critical_floor_unverified</c>. "4" stopped reporting interest coverage as <c>ratios_not_measured</c>
    /// on the renormalise path (RT 6 / RECPAM presentation, see <see cref="RatioUnreadByPresentation"/>). "5" reports
    /// a failure of ONLY <c>net_profit_reconciliation</c> as the <c>integrity_presentation</c> caveat instead of
    /// <c>integrity_failed</c> / <c>critical_floor_unverified</c> (<c>BalanceIntegrityService.PresentationChecks</c>).
    /// "6" takes a column mix out of that caveat — result before tax and income tax read from the comparative column
    /// (<c>BalanceIntegrityCheck.ColumnMix</c>) is a misread, <c>integrity_failed</c> with a message that names it.
    /// </summary>
    public const string PolicyVersion = "6";

    /// <summary>
    /// The scoring key of interest coverage, which <c>ratios_not_measured</c> leaves out on the renormalise
    /// path. Most PyME income statements follow RT 6 and present financial results together with RECPAM
    /// ("resultados financieros y por tenencia, incluido el RECPAM") on one line, so financial expenses
    /// cannot be isolated and the extraction leaves them null by design. That describes the accounting
    /// FORMAT, not missing company data: the renormalised score is as reliable as for any statement in that
    /// format, and naming it turned almost every analysis into <c>with_caveats</c>. The low-coverage fallback
    /// (<c>ratios_low_coverage</c>) still names it, because there the score really is short of data; a
    /// <c>not_meaningful</c> interest coverage is still reported by <c>ratios_not_meaningful</c>.
    /// </summary>
    public const string RatioUnreadByPresentation = "cobertura_intereses";

    /// <summary>
    /// A balance is stale when AT LEAST this many months separate 31-Dec of its fiscal year from the score
    /// date. A minimum: the closing month is not recorded, and 31-Dec is the latest it can be.
    /// </summary>
    // PROVISORIO — pendiente de decisión de la SGR (decisiones 4/7)
    public const int BalanceStaleMonths = 18;

    /// <summary>The LUFE snapshot is stale when it was taken MORE than this many months before the score date.</summary>
    // PROVISORIO — pendiente de decisión de la SGR (decisiones 4/7)
    public const int LufeSnapshotStaleMonths = 6;

    /// <summary>The LUFE sales series is stale when its latest month is MORE than this many months before the score date.</summary>
    // PROVISORIO — pendiente de decisión de la SGR (decisiones 4/7)
    public const int LufeSalesStaleMonths = 9;

    /// <summary>The CASFOG snapshot is stale when it was fetched MORE than this many months before the score date.</summary>
    // PROVISORIO — pendiente de decisión de la SGR (decisiones 4/7)
    public const int CasfogStaleMonths = 6;

    /// <summary>The BCRA Central de Deudores period lags when it is MORE than this many months before the score date.</summary>
    // PROVISORIO — pendiente de decisión de la SGR (decisiones 4/7)
    public const int BcraPeriodLagMonths = 4;

    /// <summary>A month as one integer (year × 12 + month − 1).</summary>
    public static int MonthIndex(int year, int month) => year * 12 + month - 1;

    /// <summary>
    /// A date as UTC, or null when it is unknown: the <c>DateTime</c> default (year ≤ 1, what a
    /// LookupFailed block carries) is never read as "very old". An Unspecified kind — every value read
    /// back from a SQL <c>datetime2</c> column — is taken as the UTC it was written as.
    /// </summary>
    public static DateTime? KnownUtc(DateTime? value)
    {
        if (value is not { } date || date.Year <= 1)
            return null;

        return date.Kind switch
        {
            DateTimeKind.Utc   => date,
            DateTimeKind.Local => date.ToUniversalTime(),
            _                  => DateTime.SpecifyKind(date, DateTimeKind.Utc),
        };
    }

    /// <summary>Whole calendar months from <paramref name="from"/> to <paramref name="scoreDateUtc"/>.</summary>
    public static int MonthsBefore(DateTime from, DateTime scoreDateUtc)
        => MonthIndex(scoreDateUtc.Year, scoreDateUtc.Month) - MonthIndex(from.Year, from.Month);

    /// <summary>A YYYYMM period as (year, month), or null when malformed — unknown, never "very old".</summary>
    public static (int Year, int Month)? ParsePeriodo(string? periodo)
    {
        if (PersonEvaluation.PersonEvaluationService.ParseYearMonth(periodo) is not { } first)
            return null;

        return (first.Year, first.Month);
    }
}
