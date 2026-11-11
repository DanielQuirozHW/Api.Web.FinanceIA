// Project : Api.Web.FinanceIA
// Created : 2026-10-20

using System.Globalization;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.PersonEvaluation;

/// <summary>
/// The income and sales figures the evaluation derives once from the #253 incomes read model, shared by
/// the filters, the indicators and the coverage. Pure.
/// </summary>
internal sealed class IncomeFacts
{
    /// <summary>The sales-like figure of each source, as the read model's coherence compares them.</summary>
    private static readonly IReadOnlyDictionary<string, string> SalesConceptBySource = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [LegajoIncomeSources.Iva]           = LegajoIncomeConcepts.Ventas,
        [LegajoIncomeSources.Facturacion]   = LegajoIncomeConcepts.Ventas,
        [LegajoIncomeSources.Iibb]          = LegajoIncomeConcepts.Ventas,
        [LegajoIncomeSources.Manifestacion] = LegajoIncomeConcepts.Ventas,
        [LegajoIncomeSources.Certificacion] = LegajoIncomeConcepts.IngresoCertificado
    };

    /// <summary>Sources whose sales were declared to the CREDITOR, not to the fisco.</summary>
    internal static readonly IReadOnlySet<string> CreditorSources =
        new HashSet<string>(StringComparer.Ordinal) { LegajoIncomeSources.Certificacion, LegajoIncomeSources.Manifestacion };

    /// <summary>Sources whose sales were declared to the FISCO (DDJJ or invoicing).</summary>
    internal static readonly IReadOnlySet<string> FiscalSources =
        new HashSet<string>(StringComparer.Ordinal) { LegajoIncomeSources.Iva, LegajoIncomeSources.Facturacion, LegajoIncomeSources.Iibb };

    /// <summary>Minimum months in a 12-month window for the monthly track to be used (annualised below 12).</summary>
    internal const int MinMonthsPerWindow = 6;

    /// <summary>The last 12 months of sales (primary source per period), or null when none can be built.</summary>
    public SalesWindow? SalesLevel { get; private init; }

    /// <summary>The 12 months before <see cref="SalesLevel"/>, or null.</summary>
    public SalesWindow? PreviousSales { get; private init; }

    /// <summary>The CPI month every real figure is expressed in (YYYY-MM), or null without a series.</summary>
    public string? CpiReferenceMonth { get; private init; }

    /// <summary>The first day of <see cref="CpiReferenceMonth"/>, or null without a series.</summary>
    public DateOnly? CpiReferenceDate => Cpi?.ReferenceDate;

    /// <summary>The CPI level the real figures are built from; null without a series.</summary>
    private CpiIndex? Cpi { get; init; }

    /// <summary>The latest fiscal year of Ganancias (F.711) figures, or null.</summary>
    public GananciasYear? Ganancias { get; private init; }

    /// <summary>The latest annual certified income, or null.</summary>
    public (decimal Amount, string Period, IReadOnlyList<Guid> Documents)? Certificacion { get; private init; }

    /// <summary>
    /// The manifestación's resultado (its cuadro de resultados: ingresos − gastos), from its latest period,
    /// annualised when the period is not a year; null without one. The #253 read model stores it as
    /// (Manifestacion, Otro) and keeps no split of gastos into activity and personal.
    /// </summary>
    public ManifestacionResultado? ManifestacionResultado { get; private init; }

    /// <summary>True when the legajo has sales from IVA or invoicing (verified, not only declared).</summary>
    public bool HasInvoicedSales { get; private init; }

    /// <summary>
    /// Agro annual mode only (engine 0.7): the campaign the sales window was aligned to, or why it was not.
    /// Null outside agro mode, where the window is the 0.6 one.
    /// </summary>
    public CampaignWindow? Campaign { get; private init; }

    /// <summary>
    /// Agro annual mode only: the calendar months (1-12) with zero or no declared sales in the monthly window in
    /// use — the off-season. Empty outside agro mode and when the sales level is not monthly.
    /// </summary>
    public IReadOnlySet<int> OffSeasonMonths { get; private init; } = new HashSet<int>();

    /// <summary>Builds the facts from the input.</summary>
    public static IncomeFacts From(PersonEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var cpi    = CpiIndex.From(input.InflationSeries);
        var points = SalesPoints(input.Incomes).ToList();

        var (level, previous) = MonthlyWindows(points, cpi);

        CampaignWindow? campaign = null;
        if (input.Agro is { Active: true } agro)
        {
            var aligned = CampaignWindows(points, cpi, agro.MesInicioCampania);
            campaign = aligned.Campaign;
            if (aligned.Level is not null)
                (level, previous) = (aligned.Level, aligned.Previous);
        }

        if (level is null)
            (level, previous) = AnnualWindows(points, cpi);

        return new IncomeFacts
        {
            Campaign          = campaign,
            OffSeasonMonths   = campaign is not null && level is { Granularity: "month" } ? OffSeason(points, level) : new HashSet<int>(),
            SalesLevel        = level,
            PreviousSales     = previous,
            CpiReferenceMonth = cpi?.ReferenceLabel,
            Cpi               = cpi,
            Ganancias         = LatestGanancias(input.Incomes),
            Certificacion     = LatestCertificacion(input.Incomes),
            ManifestacionResultado = LatestManifestacionResultado(input.Incomes),
            HasInvoicedSales  = input.Incomes.Periods.SelectMany(p => p.Entries).Any(e =>
                                    (e.Source == LegajoIncomeSources.Iva || e.Source == LegajoIncomeSources.Facturacion)
                                    && e.Concept == LegajoIncomeConcepts.Ventas)
        };
    }

    /// <summary>
    /// <paramref name="amount"/>, in pesos of <paramref name="month"/>, re-expressed in pesos of the CPI reference
    /// month; null without a series or when the series does not reach that month. A month after the reference
    /// is already in current pesos and comes back unchanged.
    /// </summary>
    internal decimal? ToCpiReference(DateOnly month, decimal amount) => Cpi?.ToReference(month, amount);

    /// <summary>The sales figure of one source in one period: the sum of its sales-like entries, with their documents.</summary>
    internal static (decimal Amount, IReadOnlyList<Guid> Documents)? SalesOf(LegajoIncomePeriodResponse period, string source)
    {
        if (!SalesConceptBySource.TryGetValue(source, out var concept))
            return null;

        var rows = period.Entries.Where(e => e.Source == source && e.Concept == concept).ToList();
        if (rows.Count == 0)
            return null;

        return (rows.Sum(e => e.Amount), rows.Select(e => e.DocumentId).Distinct().ToList());
    }

    /// <summary>One sales figure per period: the read model's primary sales source.</summary>
    private static IEnumerable<SalesPoint> SalesPoints(LegajoIncomesResponse incomes)
    {
        foreach (var period in incomes.Periods)
        {
            if (period.PrimarySalesSource is not { } source || SalesOf(period, source) is not { } sales)
                continue;

            yield return new SalesPoint(period.PeriodFrom, period.PeriodTo, period.Granularity, source, sales.Amount, sales.Documents);
        }
    }

    /// <summary>
    /// The monthly track: the 12 calendar months ending at the latest monthly figure, and the 12 before.
    /// A window with fewer than <see cref="MinMonthsPerWindow"/> months is not used; one with fewer than 12
    /// is annualised (sum × 12 / months) and says so.
    /// </summary>
    private static (SalesWindow? Level, SalesWindow? Previous) MonthlyWindows(IReadOnlyList<SalesPoint> points, CpiIndex? cpi)
    {
        var months = points.Where(p => p.Granularity == "month").ToList();
        if (months.Count == 0)
            return (null, null);

        var latest = months.Max(p => PersonEvaluationService.MonthIndex(p.From));

        return (MonthWindow(months, latest, cpi), MonthWindow(months, latest - 12, cpi));
    }

    /// <summary>
    /// Agro annual mode: the 12 months of the latest campaign that has ENDED by the latest monthly figure
    /// (campaign start = <paramref name="mesInicioCampania"/>), and the campaign before. Only a campaign with a
    /// figure for each of its 12 months is used — a seasonal window is never annualised, since the months it
    /// lacks are not average months. Level null when the legajo does not cover the campaign: the caller keeps
    /// the 0.6 window and <see cref="CampaignWindow.Text"/> says so. Previous null unless it is complete too.
    /// </summary>
    private static (SalesWindow? Level, SalesWindow? Previous, CampaignWindow Campaign) CampaignWindows(
        IReadOnlyList<SalesPoint> points, CpiIndex? cpi, int mesInicioCampania)
    {
        var months = points.Where(p => p.Granularity == "month").ToList();
        if (months.Count == 0)
            return (null, null, new CampaignWindow(false, null, "sin ventas mensuales en el legajo: no se alinea a la campaña"));

        var latest      = months.Max(p => PersonEvaluationService.MonthIndex(p.From));
        var endMonth    = mesInicioCampania == 1 ? 12 : mesInicioCampania - 1;
        var end         = latest - ((latest % 12 + 1 - endMonth + 12) % 12);
        var present     = months.Select(p => PersonEvaluationService.MonthIndex(p.From)).ToHashSet();
        var label       = $"{MonthLabel(end - 11)} a {MonthLabel(end)}";

        bool Covered(int endIndex) => Enumerable.Range(endIndex - 11, 12).All(present.Contains);

        if (!Covered(end))
            return (null, null, new CampaignWindow(false, label, $"últimos 12 meses (el legajo no cubre la campaña {label})"));

        return (MonthWindow(months, end, cpi),
                Covered(end - 12) ? MonthWindow(months, end - 12, cpi) : null,
                new CampaignWindow(true, label, $"campaña {label}"));
    }

    /// <summary>
    /// The calendar months (1-12), among the 12 ending at <paramref name="level"/>'s last month, with no
    /// declared sales or sales of zero: the off-season of a seasonal activity.
    /// </summary>
    private static IReadOnlySet<int> OffSeason(IReadOnlyList<SalesPoint> points, SalesWindow level)
    {
        var end = PersonEvaluationService.MonthIndex(level.To);
        var sales = points
            .Where(p => p.Granularity == "month")
            .GroupBy(p => PersonEvaluationService.MonthIndex(p.From))
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));

        return Enumerable.Range(end - 11, 12)
            .Where(index => !sales.TryGetValue(index, out var amount) || amount == 0m)
            .Select(index => index % 12 + 1)
            .ToHashSet();
    }

    /// <summary>A month index (<see cref="PersonEvaluationService.MonthIndex"/>) as YYYY-MM.</summary>
    private static string MonthLabel(int index)
        => new DateOnly(index / 12, index % 12 + 1, 1).ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>
    /// The monthly figures of the 12 calendar months ending at <paramref name="endIndex"/>; null with fewer than
    /// <see cref="MinMonthsPerWindow"/>, annualised (sum × 12 / months) below 12.
    /// </summary>
    private static SalesWindow? MonthWindow(IReadOnlyList<SalesPoint> months, int endIndex, CpiIndex? cpi)
    {
        var inWindow = months
            .Where(p => PersonEvaluationService.MonthIndex(p.From) <= endIndex
                     && PersonEvaluationService.MonthIndex(p.From) > endIndex - 12)
            .ToList();
        if (inWindow.Count < MinMonthsPerWindow)
            return null;

        var factor  = 12m / inWindow.Count;
        var nominal = inWindow.Sum(p => p.Amount) * factor;

        decimal? real = null;
        if (cpi is not null)
        {
            var deflated = inWindow.Select(p => cpi.ToReference(p.From, p.Amount)).ToList();
            real = deflated.All(d => d is not null) ? deflated.Sum(d => d!.Value) * factor : null;
        }

        return new SalesWindow
        {
            From            = inWindow.Min(p => p.From),
            To              = inWindow.Max(p => p.To),
            Nominal         = Math.Round(nominal, 2),
            Real            = real is { } r ? Math.Round(r, 2) : null,
            MonthsAvailable = inWindow.Count,
            Annualized      = inWindow.Count < 12,
            Granularity     = "month",
            Sources         = inWindow.Select(p => p.Source).Distinct().ToList(),
            Documents       = inWindow.SelectMany(p => p.Documents).Distinct().ToList()
        };
    }

    /// <summary>
    /// The annual track, when there are not enough months: the latest year-long figure (300 to 400 days,
    /// scaled to 365) and the one ending before it starts.
    /// </summary>
    private static (SalesWindow? Level, SalesWindow? Previous) AnnualWindows(IReadOnlyList<SalesPoint> points, CpiIndex? cpi)
    {
        var years = points
            .Where(p => p.Granularity != "month")
            .Select(p => (Point: p, Days: p.To.DayNumber - p.From.DayNumber + 1))
            .Where(p => p.Days is >= 300 and <= 400)
            .OrderByDescending(p => p.Point.To)
            .ToList();
        if (years.Count == 0)
            return (null, null);

        SalesWindow Build((SalesPoint Point, int Days) year)
        {
            var factor  = 365m / year.Days;
            var nominal = year.Point.Amount * factor;
            var mid     = year.Point.From.AddDays(year.Days / 2);
            var real    = cpi?.ToReference(mid, year.Point.Amount) is { } r ? r * factor : (decimal?)null;

            return new SalesWindow
            {
                From            = year.Point.From,
                To              = year.Point.To,
                Nominal         = Math.Round(nominal, 2),
                Real            = real is { } rr ? Math.Round(rr, 2) : null,
                MonthsAvailable = 12,
                Annualized      = year.Days != 365 && year.Days != 366,
                Granularity     = year.Point.Granularity,
                Sources         = [year.Point.Source],
                Documents       = year.Point.Documents
            };
        }

        var level    = years[0];
        var previous = years.Skip(1).FirstOrDefault(y => y.Point.To < level.Point.From);

        return (Build(level), previous.Point is null ? null : Build(previous));
    }

    /// <summary>The latest Ganancias year with a Renta3ra, a Renta4ta or an F.711 MontoConsumido.</summary>
    private static GananciasYear? LatestGanancias(LegajoIncomesResponse incomes)
    {
        foreach (var period in incomes.Periods)   // newest first
        {
            var ganancias = period.Entries.Where(e => e.Source == LegajoIncomeSources.Ganancias).ToList();
            var renta3ra  = ganancias.Where(e => e.Concept == LegajoIncomeConcepts.Renta3ra).ToList();
            var renta4ta  = ganancias.Where(e => e.Concept == LegajoIncomeConcepts.Renta4ta).ToList();
            var consumo   = ganancias.Where(e => e.Concept == LegajoIncomeConcepts.MontoConsumido).ToList();
            if (renta3ra.Count == 0 && renta4ta.Count == 0 && consumo.Count == 0)
                continue;

            return new GananciasYear(
                Period:         period.Period ?? $"{period.PeriodFrom:yyyy-MM-dd}/{period.PeriodTo:yyyy-MM-dd}",
                Renta3ra:       renta3ra.Count > 0 ? renta3ra.Sum(e => e.Amount) : null,
                Renta4ta:       renta4ta.Count > 0 ? renta4ta.Sum(e => e.Amount) : null,
                MontoConsumido: consumo.Count > 0 ? consumo.Sum(e => e.Amount) : null,
                Documents:      ganancias.Select(e => e.DocumentId).Distinct().ToList());
        }

        return null;
    }

    /// <summary>The latest manifestación resultado, scaled to a year when its period covers 28 to 400 days.</summary>
    private static ManifestacionResultado? LatestManifestacionResultado(LegajoIncomesResponse incomes)
    {
        foreach (var period in incomes.Periods)   // newest first
        {
            var rows = period.Entries
                .Where(e => e.Source == LegajoIncomeSources.Manifestacion && e.Concept == LegajoIncomeConcepts.Otro)
                .ToList();
            if (rows.Count == 0)
                continue;

            var days = period.PeriodTo.DayNumber - period.PeriodFrom.DayNumber + 1;
            if (days is < 28 or > 400)
                continue;

            var annualized = days is < 360 or > 366;
            var amount = rows.Sum(e => e.Amount);
            return new ManifestacionResultado(
                Amount:     Math.Round(annualized ? amount * 365m / days : amount, 2),
                Period:     period.Period ?? $"{period.PeriodFrom:yyyy-MM-dd}/{period.PeriodTo:yyyy-MM-dd}",
                Annualized: annualized,
                Documents:  rows.Select(e => e.DocumentId).Distinct().ToList());
        }

        return null;
    }

    /// <summary>The latest certified income covering about a year.</summary>
    private static (decimal Amount, string Period, IReadOnlyList<Guid> Documents)? LatestCertificacion(LegajoIncomesResponse incomes)
    {
        foreach (var period in incomes.Periods)
        {
            var days = period.PeriodTo.DayNumber - period.PeriodFrom.DayNumber + 1;
            if (days is < 300 or > 400)
                continue;

            if (SalesOf(period, LegajoIncomeSources.Certificacion) is { } certified)
            {
                return (Math.Round(certified.Amount * 365m / days, 2),
                        period.Period ?? $"{period.PeriodFrom:yyyy-MM-dd}/{period.PeriodTo:yyyy-MM-dd}",
                        certified.Documents);
            }
        }

        return null;
    }

    private sealed record SalesPoint(
        DateOnly From, DateOnly To, string Granularity, string Source, decimal Amount, IReadOnlyList<Guid> Documents);

    /// <summary>
    /// The cumulative CPI level built from the BCRA monthly series (variable 27), to re-express a peso of
    /// month M in pesos of the reference month (the latest month of the series).
    /// </summary>
    private sealed class CpiIndex
    {
        private readonly Dictionary<int, decimal> _levels;
        private readonly int _reference;

        private CpiIndex(Dictionary<int, decimal> levels, int reference)
        {
            _levels    = levels;
            _reference = reference;
        }

        public DateOnly ReferenceDate => new(_reference / 12, _reference % 12 + 1, 1);

        public string ReferenceLabel => ReferenceDate.ToString("yyyy-MM", CultureInfo.InvariantCulture);

        public static CpiIndex? From(IReadOnlyDictionary<string, double>? series)
        {
            if (series is null || series.Count == 0)
                return null;

            var months = series
                .Select(kv => (Month: PersonEvaluationService.ParseYearMonth(kv.Key), Rate: kv.Value))
                .Where(m => m.Month is not null)
                .Select(m => (Index: PersonEvaluationService.MonthIndex(m.Month!.Value), m.Rate))
                .OrderBy(m => m.Index)
                .ToList();
            if (months.Count == 0)
                return null;

            // Level at the END of each month. A gap in the series stops the chain: months before the gap
            // cannot be carried to the reference month without inventing the missing variation.
            var levels = new Dictionary<int, decimal>();
            var level  = 1m;
            var last   = months[^1].Index;
            levels[last] = level;
            for (var i = months.Count - 1; i > 0; i--)
            {
                if (months[i - 1].Index != months[i].Index - 1)
                    break;

                level /= 1m + (decimal)months[i].Rate / 100m;
                levels[months[i - 1].Index] = level;
            }

            return new CpiIndex(levels, last);
        }

        /// <summary>The amount of <paramref name="month"/> in pesos of the reference month; null outside the series.</summary>
        public decimal? ToReference(DateOnly month, decimal amount)
        {
            var index = PersonEvaluationService.MonthIndex(month);
            if (index > _reference)
                index = _reference;   // a figure later than the series is already in current pesos

            return _levels.TryGetValue(index, out var level) ? amount / level : null;
        }
    }
}

/// <summary>Twelve months of sales, nominal and in pesos of the CPI reference month.</summary>
internal sealed record SalesWindow
{
    public required DateOnly From { get; init; }
    public required DateOnly To { get; init; }
    public required decimal Nominal { get; init; }
    public decimal? Real { get; init; }
    public required int MonthsAvailable { get; init; }
    public required bool Annualized { get; init; }
    public required string Granularity { get; init; }
    public required IReadOnlyList<string> Sources { get; init; }
    public required IReadOnlyList<Guid> Documents { get; init; }
}

/// <summary>
/// The campaign an agro sales window was aligned to (<paramref name="Aligned"/>), or the one it could not be
/// aligned to; <paramref name="Text"/> is the evidence clause (ventanaCampania).
/// </summary>
internal sealed record CampaignWindow(bool Aligned, string? Label, string Text);

/// <summary>The manifestación's resultado, annual pesos.</summary>
internal sealed record ManifestacionResultado(decimal Amount, string Period, bool Annualized, IReadOnlyList<Guid> Documents);

/// <summary>One fiscal year of Ganancias figures.</summary>
internal sealed record GananciasYear(
    string Period, decimal? Renta3ra, decimal? Renta4ta, decimal? MontoConsumido, IReadOnlyList<Guid> Documents);
