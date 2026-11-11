// Project : Api.Web.FinanceIA
// Created : 2026-10-23

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

// GET /api/dashboard/portfolio. Money is NEVER summed across currencies: every figure is keyed by the
// cupo's currency from the SQL group onwards. The cupo rules are CreditLineRules' — OccupiesCupo (in SQL,
// through its translatable twin) and EffectiveStatus (in C#, on each ValidUntil date) — and the BCRA
// reading is BcraSituacionRule, the one the scoring engine applies.
public sealed partial class DashboardService
{
    private const int TopExposuresCap     = 10;
    private const int UpcomingCap         = 10;
    private const int BcraSignalsCap      = 20;
    private const int MaturityHorizonDays = 90;
    private const int HighUtilizationCap  = 10;

    // A cupo is in highUtilization from this share of its Amount, as a percentage. Deliberately wider
    // than the cupo alert: CUPO_UTILIZATION_HIGH fires only when the cupo is EXCEEDED (over 100%), since a
    // cupo at exactly 100% is the normal state; this list is the dashboard's view of cupos near the top.
    private const decimal HighUtilizationThresholdPct = 90m;

    // CreditLines.Status values, as DashboardRepository's portfolio reads filter them.
    private const string ActiveLineStatus    = "Active";
    private const string SuspendedLineStatus = "Suspended";

    /// <inheritdoc />
    public async Task<PortfolioResponse> GetPortfolioAsync(string tenantId, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var degraded = new List<string>();

        var totals = await RunSectionAsync(DashboardSections.Totals, tenantId, null, degraded,
            () => BuildTotalsAsync(tenantId, ct), ct).ConfigureAwait(false);

        var byRisk = await RunSectionAsync(DashboardSections.ByRisk, tenantId, null, degraded,
            () => BuildByRiskAsync(tenantId, ct), ct).ConfigureAwait(false);

        var topExposures = await RunSectionAsync(DashboardSections.TopExposures, tenantId, null, degraded,
            () => BuildTopExposuresAsync(tenantId, ct), ct).ConfigureAwait(false);

        var bySector = await RunSectionAsync(DashboardSections.BySector, tenantId, null, degraded,
            () => BuildBySectorAsync(tenantId, ct), ct).ConfigureAwait(false);

        var maturities = await RunSectionAsync(DashboardSections.Maturities, tenantId, null, degraded,
            () => BuildMaturitiesAsync(tenantId, nowUtc, ct), ct).ConfigureAwait(false);

        var bcraSignals = await RunSectionAsync(DashboardSections.BcraSignals, tenantId, null, degraded,
            () => BuildBcraSignalsAsync(tenantId, ct), ct).ConfigureAwait(false);

        var highUtilization = await RunSectionAsync(DashboardSections.HighUtilization, tenantId, null, degraded,
            () => BuildHighUtilizationAsync(tenantId, ct), ct).ConfigureAwait(false);

        var bcraCurrent = await RunSectionAsync(DashboardSections.BcraCurrent, tenantId, null, degraded,
            () => BuildBcraCurrentAsync(tenantId, nowUtc, ct), ct).ConfigureAwait(false);

        var annualReview = await RunSectionAsync(DashboardSections.AnnualReview, tenantId, null, degraded,
            () => BuildAnnualReviewAsync(tenantId, nowUtc, ct), ct).ConfigureAwait(false);

        // Null for a tenant not in "sgr" mode is a real answer, not a failure: only a throw degrades it.
        var sgr = await RunSectionAsync(DashboardSections.Sgr, tenantId, null, degraded,
            () => BuildSgrAsync(tenantId, nowUtc, ct), ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Dashboard portfolio for tenant {TenantId}: currencies={Currencies} exposures={Exposures} upcoming={Upcoming} bcraSignals={BcraSignals} highUtilization={HighUtilization} bcraCurrentCurrencies={BcraCurrentCurrencies} reviewOverdue={ReviewOverdue} reviewUpcoming={ReviewUpcoming} sgrOverLimit={SgrOverLimit} degradedSections={DegradedCount}",
            tenantId, totals.Value?.Count, topExposures.Value?.Sum(t => t.Items.Count), maturities.Value?.Upcoming.Count,
            bcraSignals.Value?.TotalCount, highUtilization.Value?.Sum(h => h.TotalCount), bcraCurrent.Value?.Count,
            annualReview.Value?.OverdueCount, annualReview.Value?.UpcomingCount, sgr.Value?.OverLimitCount, degraded.Count);

        return new PortfolioResponse
        {
            GeneratedAt      = nowUtc,
            DegradedSections = degraded,
            Totals           = totals.Value,
            ByRisk           = byRisk.Value,
            TopExposures     = topExposures.Value,
            BySector         = bySector.Value,
            Maturities       = maturities.Value,
            BcraSignals      = bcraSignals.Value,
            HighUtilization  = highUtilization.Value,
            BcraCurrent      = bcraCurrent.Value,
            AnnualReview     = annualReview.Value,
            Sgr              = sgr.Value
        };
    }

    private async Task<IReadOnlyList<PortfolioCurrencyTotals>> BuildTotalsAsync(string tenantId, CancellationToken ct)
    {
        var lines = await _dashboardRepository.GetLineTotalsAsync(tenantId, ct).ConfigureAwait(false);
        var used  = await _dashboardRepository.GetOccupiedByCurrencyAsync(tenantId, ct).ConfigureAwait(false);

        var utilizado = used
            .GroupBy(r => r.Currency, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount), StringComparer.Ordinal);

        return lines
            .Select(r => r.Currency)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(currency =>
            {
                var active    = lines.FirstOrDefault(r => r.Currency == currency && r.Status == ActiveLineStatus);
                var suspended = lines.FirstOrDefault(r => r.Currency == currency && r.Status == SuspendedLineStatus);
                var otorgado  = active?.Amount ?? 0m;
                var occupied  = utilizado.GetValueOrDefault(currency);

                return new PortfolioCurrencyTotals
                {
                    Currency    = currency,
                    Otorgado    = otorgado,
                    Utilizado   = occupied,
                    Disponible  = otorgado - occupied,
                    LineCount   = active?.Count ?? 0,
                    EntityCount = active?.EntityCount ?? 0,
                    Suspended   = new DashboardCountAmount { Count = suspended?.Count ?? 0, Amount = suspended?.Amount ?? 0m }
                };
            })
            .ToList();
    }

    private async Task<IReadOnlyList<PortfolioRiskByCurrency>> BuildByRiskAsync(string tenantId, CancellationToken ct)
    {
        var lines = await _dashboardRepository.GetLinesByRiskAsync(tenantId, ct).ConfigureAwait(false);
        var used  = await _dashboardRepository.GetOccupiedByRiskAsync(tenantId, ct).ConfigureAwait(false);

        var utilizado = used
            .GroupBy(r => (r.Currency, Bucket: RiskBucketOf(r.AnalysisKind, r.AiSuggestedLevel)))
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));

        return lines
            .GroupBy(r => r.Currency, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(byCurrency =>
            {
                var buckets = byCurrency
                    .GroupBy(r => RiskBucketOf(r.AnalysisKind, r.AiSuggestedLevel), StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => (Count: g.Sum(r => r.Count), Amount: g.Sum(r => r.Amount)), StringComparer.Ordinal);

                return new PortfolioRiskByCurrency
                {
                    Currency = byCurrency.Key,
                    Buckets  = DashboardRiskBuckets.Ordered
                        .Where(buckets.ContainsKey)
                        .Select(bucket => new PortfolioRiskBucket
                        {
                            Bucket    = bucket,
                            LineCount = buckets[bucket].Count,
                            Otorgado  = buckets[bucket].Amount,
                            Utilizado = utilizado.GetValueOrDefault((byCurrency.Key, bucket))
                        })
                        .ToList()
                };
            })
            .ToList();
    }

    private async Task<IReadOnlyList<PortfolioTopExposures>> BuildTopExposuresAsync(string tenantId, CancellationToken ct)
    {
        var currencies = await _dashboardRepository.GetActiveLineCurrenciesAsync(tenantId, ct).ConfigureAwait(false);

        // One capped query per currency the tenant holds cupos in — a handful, never one per entity.
        var perCurrency = new List<(string Currency, IReadOnlyList<DashboardExposureRow> Rows)>();
        foreach (var currency in currencies)
        {
            var rows = await _dashboardRepository
                .GetTopExposuresAsync(tenantId, currency, TopExposuresCap, ct)
                .ConfigureAwait(false);
            perCurrency.Add((currency, rows));
        }

        var entities = await EntityNamesAsync(tenantId, perCurrency.SelectMany(p => p.Rows).Select(r => r.EntityId), ct)
            .ConfigureAwait(false);

        return perCurrency
            .Select(p => new PortfolioTopExposures
            {
                Currency = p.Currency,
                Items    = p.Rows
                    .Select(r =>
                    {
                        entities.TryGetValue(r.EntityId, out var entity);
                        return new PortfolioExposureItem
                        {
                            EntityId         = r.EntityId,
                            EntityName       = entity?.BusinessName,
                            EntityCuit       = entity?.CUIT,
                            CreditLineId     = r.CreditLineId,
                            Otorgado         = r.Amount,
                            Utilizado        = r.Utilizado,
                            Disponible       = r.Amount - r.Utilizado,
                            AiSuggestedLevel = r.AiSuggestedLevel,
                            RiskBucket       = RiskBucketOf(r.AnalysisKind, r.AiSuggestedLevel),
                            AnalysisId       = r.AnalysisId,
                            ResultId         = r.ResultId
                        };
                    })
                    .ToList()
            })
            .ToList();
    }

    private async Task<IReadOnlyList<PortfolioHighUtilization>> BuildHighUtilizationAsync(string tenantId, CancellationToken ct)
    {
        var counts = await _dashboardRepository
            .GetHighUtilizationCountsAsync(tenantId, HighUtilizationThresholdPct, ct)
            .ConfigureAwait(false);

        // One capped query per currency that has a cupo past the threshold — a handful, never one per entity.
        var perCurrency = new List<(string Currency, int TotalCount, IReadOnlyList<DashboardExposureRow> Rows)>();
        foreach (var count in counts.Where(c => c.Count > 0).OrderBy(c => c.Currency, StringComparer.Ordinal))
        {
            var rows = await _dashboardRepository
                .GetHighUtilizationAsync(tenantId, count.Currency, HighUtilizationThresholdPct, HighUtilizationCap, ct)
                .ConfigureAwait(false);
            perCurrency.Add((count.Currency, count.Count, rows));
        }

        var entities = await EntityNamesAsync(tenantId, perCurrency.SelectMany(p => p.Rows).Select(r => r.EntityId), ct)
            .ConfigureAwait(false);

        return perCurrency
            .Select(p => new PortfolioHighUtilization
            {
                Currency   = p.Currency,
                TotalCount = p.TotalCount,
                Items      = p.Rows
                    .Select(r =>
                    {
                        entities.TryGetValue(r.EntityId, out var entity);
                        return new PortfolioHighUtilizationItem
                        {
                            EntityId       = r.EntityId,
                            EntityName     = entity?.BusinessName,
                            EntityCuit     = entity?.CUIT,
                            CreditLineId   = r.CreditLineId,
                            Otorgado       = r.Amount,
                            Utilizado      = r.Utilizado,
                            Disponible     = r.Amount - r.Utilizado,
                            UtilizationPct = UtilizationPctOf(r.Utilizado, r.Amount),
                            RiskBucket     = RiskBucketOf(r.AnalysisKind, r.AiSuggestedLevel),
                            AnalysisId     = r.AnalysisId,
                            ResultId       = r.ResultId
                        };
                    })
                    .ToList()
            })
            .ToList();
    }

    private async Task<IReadOnlyList<PortfolioSectorByCurrency>> BuildBySectorAsync(string tenantId, CancellationToken ct)
    {
        var lines = await _dashboardRepository.GetLinesBySectorAsync(tenantId, ct).ConfigureAwait(false);
        var used  = await _dashboardRepository.GetOccupiedBySectorAsync(tenantId, ct).ConfigureAwait(false);

        var utilizado = used
            .GroupBy(r => (r.Currency, Key: SectorKey(r.Sector)))
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));

        return lines
            .GroupBy(r => r.Currency, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(byCurrency => new PortfolioSectorByCurrency
            {
                Currency = byCurrency.Key,
                Sectors  = byCurrency
                    .GroupBy(r => SectorKey(r.Sector), StringComparer.Ordinal)
                    .Select(g => new PortfolioSectorItem
                    {
                        Sector    = g.Key.Length == 0 ? null : SectorLabel(g),
                        LineCount = g.Sum(r => r.Count),
                        Otorgado  = g.Sum(r => r.Amount),
                        Utilizado = utilizado.GetValueOrDefault((byCurrency.Key, g.Key))
                    })
                    .OrderBy(s => s.Sector is null)
                    .ThenByDescending(s => s.Utilizado)
                    .ThenByDescending(s => s.Otorgado)
                    .ThenBy(s => s.Sector, StringComparer.Ordinal)
                    .ToList()
            })
            .ToList();
    }

    private async Task<PortfolioMaturities> BuildMaturitiesAsync(string tenantId, DateTime nowUtc, CancellationToken ct)
    {
        // The day buckets count Argentine calendar days; whether an operation is Vencida is
        // CreditLineRules.EffectiveStatus's call, made on the UTC date exactly as the cupo screen makes it.
        // A Vigente operation therefore has ValidUntil ≥ the UTC date ≥ the Argentine date, and its day
        // count is never negative.
        var asOf = MaturityBands.AsOf(nowUtc);

        // ValidUntil < asOf + 91 days  ⇔  its date is at most 90 days away. Everything earlier is in range,
        // the Vencida ones included.
        var horizon = asOf.AddDays(MaturityHorizonDays + 1).ToDateTime(TimeOnly.MinValue);

        var groups = await _dashboardRepository
            .GetMaturityGroupsAsync(tenantId, horizon, ct)
            .ConfigureAwait(false);

        var buckets = new Dictionary<(string Currency, string Bucket), (int Count, decimal Amount)>();
        foreach (var group in groups)
        {
            var bucket = MaturityBands.BucketOf(group.ValidUntil, asOf, nowUtc);
            var key    = (group.Currency, bucket);
            var sum    = buckets.GetValueOrDefault(key);
            buckets[key] = (sum.Count + group.Count, sum.Amount + group.Amount);
        }

        DashboardCountAmount Of(string currency, string bucket)
        {
            var sum = buckets.GetValueOrDefault((currency, bucket));
            return new DashboardCountAmount { Count = sum.Count, Amount = sum.Amount };
        }

        var byCurrency = buckets.Keys
            .Select(k => k.Currency)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(currency => new PortfolioMaturitiesByCurrency
            {
                Currency   = currency,
                Days0To30  = Of(currency, MaturityBands.Days0To30),
                Days31To60 = Of(currency, MaturityBands.Days31To60),
                Days61To90 = Of(currency, MaturityBands.Days61To90),
                Vencidas   = Of(currency, CreditLineRules.Vencida)
            })
            .ToList();

        // From the first instant of the UTC day EffectiveStatus still calls Vigente; the rule is applied
        // again to the rows, so the list cannot hold an operation the cupo screen shows as Vencida.
        var upcomingRows = await _dashboardRepository
            .GetUpcomingMaturitiesAsync(tenantId, nowUtc.Date, UpcomingCap, ct)
            .ConfigureAwait(false);

        var upcoming = upcomingRows
            .Where(r => CreditLineRules.EffectiveStatus(r.ToOperation(), nowUtc) == CreditLineRules.Vigente)
            .ToList();

        var entities = await EntityNamesAsync(tenantId, upcoming.Select(r => r.EntityId), ct).ConfigureAwait(false);

        return new PortfolioMaturities
        {
            AsOfDate   = asOf,
            ByCurrency = byCurrency,
            Upcoming   = upcoming
                .Select(r =>
                {
                    entities.TryGetValue(r.EntityId, out var entity);
                    var validUntil = DateOnly.FromDateTime(r.ValidUntil!.Value);
                    return new PortfolioUpcomingMaturity
                    {
                        OperationId  = r.OperationId,
                        CreditLineId = r.CreditLineId,
                        EntityId     = r.EntityId,
                        EntityName   = entity?.BusinessName,
                        EntityCuit   = entity?.CUIT,
                        Type         = r.Type,
                        Currency     = r.Currency,
                        Amount       = r.Amount,
                        ValidUntil   = validUntil,
                        DaysUntil    = Math.Max(0, validUntil.DayNumber - asOf.DayNumber)
                    };
                })
                .ToList()
        };
    }

    private async Task<DashboardPage<PortfolioBcraSignalItem>> BuildBcraSignalsAsync(string tenantId, CancellationToken ct)
    {
        var rows = await _dashboardRepository.GetBcraComparisonsAsync(tenantId, ct).ConfigureAwait(false);

        // One comparison per entity, the selection PYME_BCRA_WORSENED reads too (BcraSignalRule).
        var signals = BcraSignalRule.PerEntity(rows)
            .Where(x => x.IsWorsened)
            .OrderByDescending(x => x.Now!.Value - x.Then!.Value)
            .ThenByDescending(x => x.Row.BcraDataUpdatedAt ?? DateTime.MinValue)
            .ThenBy(x => x.Row.EntityId)
            .ToList();

        var page     = signals.Take(BcraSignalsCap).ToList();
        var entities = await EntityNamesAsync(tenantId, page.Select(x => x.Row.EntityId), ct).ConfigureAwait(false);

        return new DashboardPage<PortfolioBcraSignalItem>
        {
            TotalCount = signals.Count,
            Items      = page
                .Select(x =>
                {
                    entities.TryGetValue(x.Row.EntityId, out var entity);
                    return new PortfolioBcraSignalItem
                    {
                        EntityId      = x.Row.EntityId,
                        EntityName    = entity?.BusinessName,
                        EntityCuit    = entity?.CUIT,
                        SituacionThen = x.Then!.Value,
                        SituacionNow  = x.Now!.Value,
                        LastQueriedAt = x.Row.BcraDataUpdatedAt,
                        AnalysisId    = x.Row.AnalysisId!.Value,
                        ResultId      = x.Row.ResultId!.Value
                    };
                })
                .ToList()
        };
    }

    // ── Portfolio rules ───────────────────────────────────────────────────────────

    /// <summary>
    /// The risk bucket of a cupo, from its analysis' kind and result level: a persona humana analysis has
    /// no score and is its own bucket; a level the engine produces is that level; anything else — no
    /// analysis, no result, no level — is Unclassified.
    /// </summary>
    internal static string RiskBucketOf(string? analysisKind, string? aiSuggestedLevel)
    {
        if (string.Equals(analysisKind, AnalysisKinds.PersonaHumana, StringComparison.Ordinal))
            return DashboardRiskBuckets.PersonaHumana;

        return aiSuggestedLevel switch
        {
            DashboardRiskBuckets.Critical => DashboardRiskBuckets.Critical,
            DashboardRiskBuckets.High     => DashboardRiskBuckets.High,
            DashboardRiskBuckets.Medium   => DashboardRiskBuckets.Medium,
            DashboardRiskBuckets.Low      => DashboardRiskBuckets.Low,
            _                             => DashboardRiskBuckets.Unclassified
        };
    }

    /// <summary>
    /// Utilizado as a percentage of a positive <paramref name="amount"/>, to one decimal, half away from
    /// zero. Display only: whether a cupo is listed was already decided in SQL on the exact amounts.
    /// </summary>
    internal static decimal UtilizationPctOf(decimal utilizado, decimal amount)
        => Math.Round(utilizado * 100m / amount, 1, MidpointRounding.AwayFromZero);

    /// <summary>The grouping key of a sector: trimmed and case-folded; empty for no sector.</summary>
    private static string SectorKey(string? sector)
        => string.IsNullOrWhiteSpace(sector) ? string.Empty : sector.Trim().ToUpperInvariant();

    /// <summary>The spelling a folded sector is shown with: the trimmed variant on the most cupos, then the ordinal first.</summary>
    private static string SectorLabel(IEnumerable<DashboardLineGroupRow> variants)
        => variants
            .GroupBy(r => r.Sector!.Trim(), StringComparer.Ordinal)
            .OrderByDescending(g => g.Sum(r => r.Count))
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .First()
            .Key;
}
