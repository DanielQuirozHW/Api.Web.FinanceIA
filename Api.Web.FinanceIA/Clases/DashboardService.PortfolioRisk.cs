// Project : Api.Web.FinanceIA
// Created : 2026-10-25

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

// GET /api/dashboard/portfolio, the risk blocks: bcraCurrent, annualReview and sgr. As in the rest of the
// portfolio, money is never summed across currencies — except in sgr, which converts USD into ARS with the
// tenant's own exchange rate and says so — and every rule is borrowed: the BCRA reading is
// BcraSignalRule.Situacion, the review is AnnualReviewRule over PYME_REVIEW_DUE's own query, and Vigente /
// Vencida are CreditLineRules.EffectiveStatus.
public sealed partial class DashboardService
{
    private const int AnnualReviewCap = 10;
    private const int SgrOverLimitCap = 10;

    // An entity's BCRA snapshot is stale once its last query is MORE than this many Argentine calendar days old.
    private const int BcraStaleDays = 30;

    // A socio partícipe's exposure is over the limit when it is strictly above this share of the Fondo de
    // Riesgo, as a percentage. Exactly 5% is not over.
    private const decimal SgrSocioLimitPct = 5m;

    private const string ArsCurrency = "ARS";
    private const string UsdCurrency = "USD";

    private async Task<IReadOnlyList<PortfolioBcraCurrent>> BuildBcraCurrentAsync(
        string tenantId, DateTime nowUtc, CancellationToken ct)
    {
        var rows = await _dashboardRepository.GetBcraCurrentAsync(tenantId, ct).ConfigureAwait(false);
        var asOf = MaturityBands.AsOf(nowUtc);

        return rows
            .GroupBy(r => r.Currency, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(byCurrency =>
            {
                // One entry per entity: its cupos of this currency share one entity row, so one reading.
                var entities = byCurrency
                    .GroupBy(r => r.EntityId)
                    .Select(g =>
                    {
                        var first = g.First();
                        return new
                        {
                            Situacion = BcraSignalRule.Situacion(first.NowEstadoRaw, first.NowSituacionMaximaRaw),
                            first.BcraDataUpdatedAt,
                            Otorgado  = g.Sum(r => r.Amount),
                            Utilizado = g.Sum(r => r.Utilizado)
                        };
                    })
                    .ToList();

                return new PortfolioBcraCurrent
                {
                    Currency = byCurrency.Key,
                    Buckets  = entities
                        .GroupBy(e => e.Situacion)
                        .OrderBy(g => g.Key is null)
                        .ThenBy(g => g.Key)
                        .Select(g => new PortfolioBcraBucket
                        {
                            Situacion   = g.Key,
                            EntityCount = g.Count(),
                            Otorgado    = g.Sum(e => e.Otorgado),
                            Utilizado   = g.Sum(e => e.Utilizado)
                        })
                        .ToList(),
                    StaleEntityCount  = entities.Count(e => e.BcraDataUpdatedAt is { } at && IsBcraStale(at, asOf)),
                    NeverQueriedCount = entities.Count(e => e.BcraDataUpdatedAt is null),
                    OldestQueryAt     = entities.Min(e => e.BcraDataUpdatedAt)
                };
            })
            .ToList();
    }

    private async Task<PortfolioAnnualReview> BuildAnnualReviewAsync(string tenantId, DateTime nowUtc, CancellationToken ct)
    {
        var sources = await _dashboardRepository
            .GetLatestApprovedAnalysesOfActiveEntitiesAsync(tenantId, ct)
            .ConfigureAwait(false);

        var asOf    = MaturityBands.AsOf(nowUtc);
        var without = 0;
        var flagged = new List<(Guid EntityId, AnnualReviewStatus Review)>();

        foreach (var source in sources)
        {
            // An Active cupo with no approved analysis is legacy or DBA data: no date to count from, so it is
            // counted apart rather than given a guessed one. PYME_REVIEW_DUE skips it the same way.
            if (source.AnalysisId is null || source.ApprovedAt is not { } approvedAt)
            {
                without++;
                continue;
            }

            if (AnnualReviewRule.Evaluate(approvedAt, asOf) is { } review)
                flagged.Add((source.EntityId, review));
        }

        // Every overdue DaysDelta (≥ 0) is above every upcoming one (< 0), so one ordering puts overdue first.
        var page = flagged
            .OrderByDescending(f => f.Review.DaysDelta)
            .ThenBy(f => f.EntityId)
            .Take(AnnualReviewCap)
            .ToList();

        var entities = await EntityNamesAsync(tenantId, page.Select(f => f.EntityId), ct).ConfigureAwait(false);

        return new PortfolioAnnualReview
        {
            OverdueCount                 = flagged.Count(f => f.Review.IsDue),
            UpcomingCount                = flagged.Count(f => !f.Review.IsDue),
            WithoutApprovedAnalysisCount = without,
            Items                        = page
                .Select(f =>
                {
                    entities.TryGetValue(f.EntityId, out var entity);
                    return new PortfolioAnnualReviewItem
                    {
                        EntityId       = f.EntityId,
                        EntityName     = entity?.BusinessName,
                        EntityCuit     = entity?.CUIT,
                        LastApprovedAt = f.Review.ApprovedOn,
                        DueDate        = f.Review.DueDate,
                        DaysDelta      = f.Review.DaysDelta,
                        Severity       = f.Review.Severity
                    };
                })
                .ToList()
        };
    }

    private async Task<PortfolioSgr?> BuildSgrAsync(string tenantId, DateTime nowUtc, CancellationToken ct)
    {
        var mode = await _tenantSettings.GetCreditLineModeAsync(tenantId, ct).ConfigureAwait(false);
        if (!string.Equals(mode, TenantSettingKeys.CreditLineModeSgr, StringComparison.Ordinal))
            return null;

        var settings   = await _sgrSettings.GetAsync(tenantId, ct).ConfigureAwait(false);
        var operations = await _dashboardRepository
            .GetOccupyingOperationsOfActiveLinesAsync(tenantId, ct)
            .ConfigureAwait(false);

        var rate            = settings.TipoCambioUsd;
        var saldo           = 0m;
        var usdExcluded     = 0m;
        var vencidasArs     = 0m;
        var vencidas        = 0;
        var exposure        = new Dictionary<Guid, decimal>();
        var otherCurrencies = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var row in operations)
        {
            // In C#, on every row, exactly as the cupo screen decides it: an Aprobada operation never expires
            // and is not issued, so it is in neither figure; a Cancelada one never reaches here.
            var status    = CreditLineRules.EffectiveStatus(row.ToOperation(), nowUtc);
            var isVigente = string.Equals(status, CreditLineRules.Vigente, StringComparison.Ordinal);
            if (!isVigente && !string.Equals(status, CreditLineRules.Vencida, StringComparison.Ordinal))
                continue;

            decimal? ars = row.Currency switch
            {
                ArsCurrency                       => row.Amount,
                UsdCurrency when rate is { } usd => row.Amount * usd,
                _                                 => null
            };

            if (ars is not { } amountArs)
            {
                if (string.Equals(row.Currency, UsdCurrency, StringComparison.Ordinal))
                {
                    if (isVigente) usdExcluded += row.Amount;
                }
                else
                {
                    otherCurrencies[row.Currency] = otherCurrencies.GetValueOrDefault(row.Currency) + 1;
                }

                continue;
            }

            if (isVigente)
            {
                saldo += amountArs;
                exposure[row.EntityId] = exposure.GetValueOrDefault(row.EntityId) + amountArs;
            }
            else
            {
                vencidas++;
                vencidasArs += amountArs;
            }
        }

        foreach (var (currency, count) in otherCurrencies)
        {
            _logger.LogWarning(
                "Dashboard sgr section for tenant {TenantId}: {OperationCount} operations in {Currency} have no conversion to ARS and are excluded.",
                tenantId, count, currency);
        }

        var fondo = settings.FondoRiesgoDisponible;

        int? overLimitCount = null;
        IReadOnlyList<PortfolioSgrOverLimitItem>? overLimitItems = null;
        if (fondo is { } fdr)
        {
            // On the exact amounts — exposure × 100 > FdR × 5 — so no rounding decides who is over.
            var over = exposure
                .Where(e => e.Value * 100m > fdr * SgrSocioLimitPct)
                .OrderByDescending(e => e.Value)
                .ThenBy(e => e.Key)
                .ToList();

            var page     = over.Take(SgrOverLimitCap).ToList();
            var entities = await EntityNamesAsync(tenantId, page.Select(e => e.Key), ct).ConfigureAwait(false);

            overLimitCount = over.Count;
            overLimitItems = page
                .Select(e =>
                {
                    entities.TryGetValue(e.Key, out var entity);
                    return new PortfolioSgrOverLimitItem
                    {
                        EntityId      = e.Key,
                        EntityName    = entity?.BusinessName,
                        EntityCuit    = entity?.CUIT,
                        ExposicionArs = Money(e.Value),
                        PctDelFondo   = Math.Round(e.Value * 100m / fdr, 1, MidpointRounding.AwayFromZero)
                    };
                })
                .ToList();
        }

        return new PortfolioSgr
        {
            Configured                = fondo.HasValue,
            FondoRiesgoDisponible     = fondo,
            FondoRiesgoFecha          = fondo.HasValue ? settings.FondoRiesgoFecha : null,
            TipoCambioUsd             = rate,
            TipoCambioFecha           = rate.HasValue ? settings.TipoCambioFecha : null,
            SaldoGarantiasVigentesArs = Money(saldo),
            UsdIncluded               = rate.HasValue,
            UsdExcludedAmount         = rate.HasValue ? null : Money(usdExcluded),
            GradoUtilizacion          = fondo is { } f1 ? Math.Round(saldo / f1, 2, MidpointRounding.AwayFromZero) : null,
            LimitePorSocio            = fondo is { } f2 ? Money(f2 * SgrSocioLimitPct / 100m) : null,
            OverLimitCount            = overLimitCount,
            OverLimit                 = overLimitItems,
            VencidasSinCerrarCount    = vencidas,
            VencidasSinCerrarArs      = Money(vencidasArs),
            MoraDisponible            = false
        };
    }

    /// <summary>
    /// Whether a BCRA snapshot queried at <paramref name="queriedAtUtc"/> is stale on <paramref name="asOf"/>:
    /// more than <see cref="BcraStaleDays"/> Argentine calendar days between the two dates.
    /// </summary>
    internal static bool IsBcraStale(DateTime queriedAtUtc, DateOnly asOf)
        => asOf.DayNumber - MaturityBands.AsOf(queriedAtUtc).DayNumber > BcraStaleDays;

    /// <summary>A money figure as shown: 2 decimals, half away from zero. Comparisons use the exact amounts.</summary>
    private static decimal Money(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}
