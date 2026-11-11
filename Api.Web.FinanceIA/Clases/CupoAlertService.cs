// Project : Api.Web.FinanceIA
// Created : 2026-10-25

using System.Collections.Concurrent;
using System.Globalization;
using Api.Web.FinanceIA.Clases.Time;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Default implementation of <see cref="ICupoAlertService"/>: every event alert of
/// <see cref="EventAlertTypes.All"/>, the cupo-level ones and — despite the name, kept so the controller,
/// DI and the tests did not churn — the PyME-level ones too.
///
/// <para>
/// Evaluate-on-read, like <see cref="BillingAlertService"/>: driven from GET /api/alerts (the
/// full-tenant listing) before the list is read, so the response already reflects it. Registered
/// Scoped, like the repositories it reads and writes through.
/// </para>
///
/// <para>
/// IF NOTHING CHANGED OR NOTHING HAS TO BE DONE, IT IS NOT AN ALERT. A cupo at exactly 100% is the normal
/// state of a cupo approved for one operation of its full amount, so utilization alerts only when the cupo
/// is EXCEEDED; every type below names the action that clears it.
/// </para>
///
/// <para>
/// THE RULES ARE THE CUPO CARD'S AND THE DASHBOARD'S. Active cupos only; an operation occupies cupo per
/// <see cref="CreditLineRules.OccupiesCupo"/> (applied in SQL through its translatable twin) and is
/// 'Vencida' per <see cref="CreditLineRules.EffectiveStatus(CreditLineOperation, DateTime)"/>; Utilizado and
/// Disponible are computed exactly as <c>CreditLinesController.MapCreditLine</c> computes them; "por vencer"
/// is the dashboard's Days0To30 band (<see cref="MaturityBands"/>) and "BCRA empeoró" is its bcraSignals
/// selection (<see cref="BcraSignalRule"/>). An alert that disagreed with the screen it points at would be
/// worse than no alert.
/// </para>
///
/// <para>
/// IDENTITY. One OPEN alert per (TenantId, AlertType, key), the key carried in MetricKey as
/// <see cref="EventAlertTypes.CupoMetricKey"/> or <see cref="EventAlertTypes.EntityMetricKey"/>. dbo.Alerts
/// has no unique index to enforce that, so this pass does: it never inserts a second open alert for a key,
/// and if two ever exist it keeps the newest and resolves the rest.
/// </para>
///
/// <para>
/// A BOUNDED PASS. Whatever the size of the tenant: the Active cupos and their operations (2 statements),
/// the open event alerts (1), the BCRA comparison rows the dashboard reads (1), the latest approved analysis
/// per PyME (1), the open billing alerts of past periods (1), the history of the candidate keys (1, only when
/// there are candidates) and one SaveChanges. Never a query per cupo, per PyME or per alert.
/// </para>
///
/// <para>
/// PAST-PERIOD BILLING ALERTS. The same pass also closes the plan alerts of
/// <see cref="BillingAlertService.AllTypes"/> left open from an earlier billing period: each one describes
/// one period's quota, and a new period is a new quota, so the situation it named no longer exists. "Earlier"
/// is CreatedAt before the start of the current period, computed by <see cref="ArgentineTime.CurrentPeriod"/>
/// exactly as the usage snapshot those alerts were emitted from computes it. They are resolved by the system
/// (ResolvedBy null), in the pass's one SaveChanges; the notifications already fanned out are not touched.
/// Running here, ahead of the listing, the inbox is right the moment it loads.
/// </para>
///
/// <para>
/// No Notifications fan-out: the bell is for per-user "your turn" events, and these are a standing
/// state of the portfolio that the alerts inbox already shows.
/// </para>
/// </summary>
public sealed class CupoAlertService : ICupoAlertService
{
    private const decimal ExceededThreshold = 100m;
    private const decimal ExpiredThreshold  = 0m;
    private const decimal ExpiringThreshold = MaturityBands.FirstBandDays;

    // PYME_BCRA_WORSENED is High from situación 3 ("con problemas") onwards.
    private const int BcraHighSituacion = 3;

    // decimal(10,4) tops out at 999999.9999, but a percentage above 999.9999 says nothing more than
    // "far over the cupo" and the brief caps it there; the copy still prints the real figures.
    private const decimal MaxMetricValue = 999.9999m;

    // dbo.Alerts.Severity is constrained by CK_Alerts_Severity to TitleCase spellings, and
    // AlertsController counts severities by case-sensitive string equality — see BillingAlertService.
    private const string HighSeverity   = "High";
    private const string MediumSeverity = "Medium";

    // Long enough for a real pass to finish behind a concurrent one, short enough that a pass stuck on
    // the database cannot hold every alerts poll of the tenant hostage: on timeout the listing is
    // served as it stands.
    private static readonly TimeSpan DefaultGateTimeout = TimeSpan.FromSeconds(10);

    // The Alerts page and the sidebar badge call GET /api/alerts at the same time, and two concurrent
    // passes would both see "no open alert" and both insert one. One gate per tenant, held across the
    // read-decide-write of a pass. Static because the service is Scoped. This serialises within ONE
    // instance only — the same single-instance assumption the background workers make (see
    // LegajoPipelineWorker / LegajoOptions.StaleMinutes). With a scale-out two instances could still
    // race; the duplicate self-heal in ReconcileCoreAsync then resolves the extra on the next pass.
    // Grows by one entry per tenant ever reconciled, which is bounded by the tenant registry.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> TenantGates = new(StringComparer.Ordinal);

    // Copy is es-AR: this text goes straight to an Argentine analyst in the alerts inbox.
    private static readonly CultureInfo EsAr = CultureInfo.GetCultureInfo("es-AR");

    private readonly ICreditLineRepository     _creditLineRepository;
    private readonly IDashboardRepository      _dashboardRepository;
    private readonly IAlertRepository          _alertRepository;
    private readonly ILogger<CupoAlertService> _logger;
    private readonly TimeSpan                  _gateTimeout;

    /// <summary>
    /// Initializes a new instance of <see cref="CupoAlertService"/>.
    /// </summary>
    public CupoAlertService(
        ICreditLineRepository     creditLineRepository,
        IDashboardRepository      dashboardRepository,
        IAlertRepository          alertRepository,
        ILogger<CupoAlertService> logger)
        : this(creditLineRepository, dashboardRepository, alertRepository, logger, DefaultGateTimeout)
    {
    }

    // Test seam (InternalsVisibleTo): a short gate timeout, so a test can prove a pass that cannot enter
    // the gate returns no facts without waiting the production 10 s.
    internal CupoAlertService(
        ICreditLineRepository     creditLineRepository,
        IDashboardRepository      dashboardRepository,
        IAlertRepository          alertRepository,
        ILogger<CupoAlertService> logger,
        TimeSpan                  gateTimeout)
    {
        _creditLineRepository = creditLineRepository;
        _dashboardRepository  = dashboardRepository;
        _alertRepository      = alertRepository;
        _logger               = logger;
        _gateTimeout          = gateTimeout;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, AlertFactsDto>?> ReconcileAsync(
        string tenantId, DateTime nowUtc, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return null;

        var gate    = TenantGates.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
        var entered = false;

        try
        {
            entered = await gate.WaitAsync(_gateTimeout, ct).ConfigureAwait(false);
            if (!entered)
            {
                _logger.LogWarning(
                    "Event alert reconciliation skipped for tenant {TenantId}: another pass held the tenant gate for more than {TimeoutSeconds}s. The alerts listing is served as it stands.",
                    tenantId, _gateTimeout.TotalSeconds);
                return null;
            }

            return await ReconcileCoreAsync(tenantId, nowUtc, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller hung up. Not a failure of this pass, and the request is over anyway.
            throw;
        }
        catch (Exception ex)
        {
            // Deliberately swallowed: this runs ahead of the alerts listing, and a reconciliation that
            // fails must never turn a working GET /api/alerts into a 500. The alerts already stored are
            // still served; the next poll tries again.
            _logger.LogWarning(
                ex,
                "Event alert reconciliation failed for tenant {TenantId}. The alerts listing is unaffected.",
                tenantId);
            return null;
        }
        finally
        {
            if (entered) gate.Release();
        }
    }

    private async Task<IReadOnlyDictionary<Guid, AlertFactsDto>> ReconcileCoreAsync(
        string tenantId, DateTime nowUtc, CancellationToken ct)
    {
        var cupos = await _creditLineRepository
            .GetActiveForCupoAlertsAsync(tenantId, ct)
            .ConfigureAwait(false);

        // Both PyME-level inputs are scoped to PyMEs with an Active cupo; with none there is nothing to
        // read, and every PyME alert still open resolves below.
        IReadOnlyList<DashboardBcraComparisonRow> bcraRows = Array.Empty<DashboardBcraComparisonRow>();
        IReadOnlyList<PymeReviewSource>           reviews  = Array.Empty<PymeReviewSource>();
        if (cupos.Count > 0)
        {
            bcraRows = await _dashboardRepository
                .GetBcraComparisonsAsync(tenantId, ct)
                .ConfigureAwait(false);

            reviews = await _creditLineRepository
                .GetLatestApprovedAnalysesOfActiveEntitiesAsync(tenantId, ct)
                .ConfigureAwait(false);
        }

        var open = await _alertRepository
            .GetOpenByTypesForUpdateAsync(tenantId, EventAlertTypes.All, ct)
            .ConfigureAwait(false);

        var resolvedCount = 0;
        var updatedCount  = 0;

        // ── Billing alerts of past periods: over, so resolved.
        var billingClosedCount = 0;
        foreach (var stale in await LoadPastPeriodBillingAlertsAsync(tenantId, nowUtc, ct).ConfigureAwait(false))
        {
            AutoResolve(stale, nowUtc);
            billingClosedCount++;
        }

        // ── Index the open alerts; self-heal duplicates (newest wins).
        var openByKey = new Dictionary<(string AlertType, string MetricKey), Alert>();
        foreach (var group in open.GroupBy(a => (a.AlertType, MetricKey: a.MetricKey ?? string.Empty)))
        {
            var ordered = group
                .OrderByDescending(a => a.CreatedAt)
                .ThenByDescending(a => a.AlertId)
                .ToList();

            openByKey[group.Key] = ordered[0];

            foreach (var duplicate in ordered.Skip(1))
            {
                AutoResolve(duplicate, nowUtc);
                resolvedCount++;
            }
        }

        // ── What should be open right now.
        var desired   = new Dictionary<(string AlertType, string MetricKey), EventCondition>();
        var untouched = new HashSet<(string AlertType, string MetricKey)>();

        void Want(EventCondition? condition)
        {
            if (condition is not null)
                desired[(condition.Alert.AlertType, condition.Alert.MetricKey!)] = condition;
        }

        var asOf = MaturityBands.AsOf(nowUtc);

        foreach (var cupo in cupos)
        {
            var metricKey = EventAlertTypes.CupoMetricKey(cupo.CreditLineId);

            Want(EvaluateExpired(tenantId, cupo, metricKey, nowUtc));
            Want(EvaluateExpiring(tenantId, cupo, metricKey, asOf, nowUtc));

            if (cupo.Amount <= 0m)
            {
                // Create and raise both refuse a non-positive amount, so this is legacy or DBA data.
                // No percentage exists to judge it by, so its utilization alert, if any, is left as
                // it is rather than resolved on a figure that was never computed.
                _logger.LogWarning(
                    "Cupo {CreditLineId} of tenant {TenantId} has a non-positive amount; utilization alert not evaluated.",
                    cupo.CreditLineId, tenantId);
                untouched.Add((EventAlertTypes.CupoUtilizationHigh, metricKey));
                continue;
            }

            Want(EvaluateExceeded(tenantId, cupo, metricKey, nowUtc));
        }

        foreach (var review in reviews)
        {
            var metricKey = EventAlertTypes.EntityMetricKey(review.EntityId);

            if (review.AnalysisId is not { } analysisId || review.ApprovedAt is not { } approvedAt)
            {
                // An Active cupo is granted from an approved analysis, so this is legacy or DBA data. With
                // no date to count from, its review alert, if any, is left as it is.
                _logger.LogWarning(
                    "Entity {EntityId} of tenant {TenantId} has an Active cupo but no approved analysis; review alert not evaluated.",
                    review.EntityId, tenantId);
                untouched.Add((EventAlertTypes.PymeReviewDue, metricKey));
                continue;
            }

            Want(EvaluateReview(tenantId, review.EntityId, analysisId, approvedAt, metricKey, asOf, nowUtc));
        }

        foreach (var comparison in BcraSignalRule.PerEntity(bcraRows))
        {
            var metricKey = EventAlertTypes.EntityMetricKey(comparison.Row.EntityId);

            if (!comparison.IsComparable)
            {
                // One side reads no situación (a failed lookup, no Deuda block): "unknown", not "better".
                // Resolving on it would make a transient BCRA outage close the alert and reopen it, unread,
                // on the next good reading.
                untouched.Add((EventAlertTypes.PymeBcraWorsened, metricKey));
                continue;
            }

            Want(EvaluateBcra(tenantId, comparison, metricKey, nowUtc));
        }

        // ── Open, but the condition no longer holds — or the cupo / PyME is out of scope: resolve.
        foreach (var (key, alert) in openByKey)
        {
            if (desired.ContainsKey(key) || untouched.Contains(key)) continue;

            AutoResolve(alert, nowUtc);
            resolvedCount++;
        }

        // ── Open and still true: update in place. Never a second open alert.
        // The facts of every alert this pass leaves open, keyed by the AlertId the listing will read.
        var facts      = new Dictionary<Guid, AlertFactsDto>();
        var candidates = new List<EventCondition>();
        foreach (var (key, condition) in desired)
        {
            if (openByKey.TryGetValue(key, out var existing))
            {
                if (UpdateInPlace(existing, condition.Alert))
                    updatedCount++;
                facts[existing.AlertId] = condition.Facts;
            }
            else
            {
                candidates.Add(condition);
            }
        }

        // ── True with nothing open: create, unless a user dismissed it and it has not got worse.
        var toCreate = new List<Alert>();
        if (candidates.Count > 0)
        {
            var latest = await _alertRepository
                .GetLatestByMetricKeysAsync(
                    tenantId,
                    EventAlertTypes.All,
                    candidates.Select(c => c.Alert.MetricKey!).Distinct(StringComparer.Ordinal).ToList(),
                    ct)
                .ConfigureAwait(false);

            foreach (var candidate in candidates)
            {
                var key = (candidate.Alert.AlertType, candidate.Alert.MetricKey!);
                if (latest.TryGetValue(key, out var previous) && IsSuppressed(previous, candidate))
                    continue;

                toCreate.Add(candidate.Alert);
                facts[candidate.Alert.AlertId] = candidate.Facts;
            }
        }

        if (toCreate.Count == 0 && updatedCount == 0 && resolvedCount == 0 && billingClosedCount == 0) return facts;

        await _alertRepository
            .SaveReconciliationAsync(tenantId, toCreate, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Reconciled event alerts for tenant {TenantId}: {CreatedCount} created, {UpdatedCount} updated, {ResolvedCount} resolved, {BillingClosedCount} past-period billing alert(s) closed",
            tenantId, toCreate.Count, updatedCount, resolvedCount, billingClosedCount);

        return facts;
    }

    /// <summary>
    /// The tenant's OPEN billing alerts created before the current billing period started, tracked, so the
    /// pass resolves them and its one SaveChanges persists them.
    /// </summary>
    /// <remarks>
    /// Fault-isolated: a failed read is logged and yields none, so it never costs the event alerts their
    /// pass, and never the listing its 200. Only a cancellation of the request propagates.
    /// </remarks>
    private async Task<IReadOnlyList<Alert>> LoadPastPeriodBillingAlertsAsync(
        string tenantId, DateTime nowUtc, CancellationToken ct)
    {
        try
        {
            return await _alertRepository
                .GetOpenByTypesCreatedBeforeForUpdateAsync(
                    tenantId, BillingAlertService.AllTypes, ArgentineTime.CurrentPeriod(nowUtc).StartUtc, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to read the past-period billing alerts of tenant {TenantId}; they stay open until the next pass. The event alerts are reconciled regardless.",
                tenantId);
            return Array.Empty<Alert>();
        }
    }

    /// <summary>
    /// CUPO_OPERATIONS_EXPIRED: the cupo's effective-'Vencida' operations, which still occupy cupo until
    /// someone closes them. Null when there are none.
    /// </summary>
    /// <remarks>
    /// Worse, for a dismissal, means a higher count — or any operation that expired AFTER the user
    /// resolved the alert: otherwise dismissing "2 vencidas" and later swapping one of them for a newly
    /// expired one would stay silent at the same count forever. That check is the same rule again,
    /// <see cref="CreditLineRules.EffectiveStatus(CreditLineOperation, DateTime)"/> asked as of the
    /// resolution instant.
    /// </remarks>
    private static EventCondition? EvaluateExpired(
        string tenantId, CupoAlertSource cupo, string metricKey, DateTime nowUtc)
    {
        var expired = cupo.OccupyingOperations
            .Where(op => CreditLineRules.EffectiveStatus(op, nowUtc) == CreditLineRules.Vencida)
            .ToList();

        if (expired.Count == 0) return null;

        var occupied = expired.Sum(op => op.Amount);
        // EffectiveStatus only derives 'Vencida' from a ValidUntil in the past, so every one has a value.
        var oldest = expired.Min(op => op.ValidUntil!.Value);

        var description = expired.Count == 1
            ? $"1 operación vencida sigue ocupando {Money(cupo.Currency, occupied)} de un cupo de {Money(cupo.Currency, cupo.Amount)}. Venció el {Date(oldest)}. Cerrala para liberar el cupo."
            : $"{expired.Count} operaciones vencidas siguen ocupando {Money(cupo.Currency, occupied)} de un cupo de {Money(cupo.Currency, cupo.Amount)}. La más antigua venció el {Date(oldest)}. Cerralas para liberar el cupo.";

        var alert = BuildAlert(
            tenantId, cupo.EntityId, cupo.AnalysisId, metricKey, nowUtc,
            EventAlertTypes.CupoOperationsExpired,
            HighSeverity,
            "Operaciones vencidas ocupando cupo",
            description,
            metricValue: expired.Count,
            thresholdValue: ExpiredThreshold);

        var facts = new AlertFactsDto
        {
            Currency   = cupo.Currency,
            Amount     = occupied,
            CupoAmount = cupo.Amount,
            Count      = expired.Count,
            Date       = DateOnly.FromDateTime(oldest)
        };

        return new EventCondition(alert, facts, previous =>
            MetricOrSeverityUp(alert, previous)
            || (previous.ResolvedAt is { } resolvedAt
                && expired.Any(op => CreditLineRules.EffectiveStatus(op, resolvedAt) != CreditLineRules.Vencida)));
    }

    /// <summary>
    /// CUPO_OPERATIONS_EXPIRING: the cupo's 'Vigente' operations due within the dashboard's Days0To30 band —
    /// <see cref="MaturityBands.BucketOf"/> on the same Argentine <paramref name="asOf"/>. Null when there are
    /// none. Medium: nothing is wrong yet, but something has to be done before it is.
    /// </summary>
    /// <remarks>
    /// Worse, for a dismissal, means an operation in the window now that was NOT in it when the user
    /// resolved the alert: one created or confirmed 'Vigente' after that instant, or one whose date was then
    /// still beyond the band. The count alone would miss a swap at the same count; the window growing by a
    /// day is exactly what the dismissal is about.
    /// </remarks>
    private static EventCondition? EvaluateExpiring(
        string tenantId, CupoAlertSource cupo, string metricKey, DateOnly asOf, DateTime nowUtc)
    {
        var expiring = cupo.OccupyingOperations
            .Where(op => op.ValidUntil.HasValue
                      && CreditLineRules.EffectiveStatus(op, nowUtc) == CreditLineRules.Vigente
                      && MaturityBands.BucketOf(op.ValidUntil.Value, asOf, nowUtc) == MaturityBands.Days0To30)
            .ToList();

        if (expiring.Count == 0) return null;

        var amount  = expiring.Sum(op => op.Amount);
        var nearest = expiring.Min(op => op.ValidUntil!.Value);

        var description = expiring.Count == 1
            ? $"1 operación vence en los próximos 30 días por {Money(cupo.Currency, amount)}. Vence el {Date(nearest)}. Renová la operación o cerrala cuando termine."
            : $"{expiring.Count} operaciones vencen en los próximos 30 días por {Money(cupo.Currency, amount)}. La más próxima vence el {Date(nearest)}. Renová la operación o cerrala cuando termine.";

        var alert = BuildAlert(
            tenantId, cupo.EntityId, cupo.AnalysisId, metricKey, nowUtc,
            EventAlertTypes.CupoOperationsExpiring,
            MediumSeverity,
            "Operaciones por vencer",
            description,
            metricValue: expiring.Count,
            thresholdValue: ExpiringThreshold);

        var facts = new AlertFactsDto
        {
            Currency   = cupo.Currency,
            Amount     = amount,
            CupoAmount = cupo.Amount,
            Count      = expiring.Count,
            Date       = DateOnly.FromDateTime(nearest)
        };

        return new EventCondition(alert, facts, previous =>
            previous.ResolvedAt is { } resolvedAt
            && expiring.Any(op => !WasExpiringAt(op, resolvedAt)));
    }

    /// <summary>
    /// CUPO_UTILIZATION_HIGH: the cupo is EXCEEDED, Utilizado above Amount — Disponible strictly negative.
    /// Exactly 100% is the normal state of a cupo granted for one operation of its full amount and does not
    /// alert. Null otherwise. The caller has already ruled out a non-positive Amount.
    /// </summary>
    private static EventCondition? EvaluateExceeded(
        string tenantId, CupoAlertSource cupo, string metricKey, DateTime nowUtc)
    {
        var utilizado  = cupo.OccupyingOperations.Sum(op => op.Amount);
        var disponible = cupo.Amount - utilizado;

        // On the exact amounts, not on a rounded percentage: 100.00001% alerts, 100% never does.
        if (disponible >= 0m) return null;

        var percent = utilizado / cupo.Amount * 100m;

        var description =
            $"Utilizado: {Money(cupo.Currency, utilizado)} de un cupo de {Money(cupo.Currency, cupo.Amount)}. " +
            $"Disponible: {Money(cupo.Currency, disponible)}. Revisá las operaciones: cerrá las que ya terminaron o ampliá el cupo.";

        var alert = BuildAlert(
            tenantId, cupo.EntityId, cupo.AnalysisId, metricKey, nowUtc,
            EventAlertTypes.CupoUtilizationHigh,
            HighSeverity,
            "Cupo excedido",
            description,
            metricValue: Math.Min(Math.Round(percent, 4, MidpointRounding.AwayFromZero), MaxMetricValue),
            thresholdValue: ExceededThreshold);

        var facts = new AlertFactsDto
        {
            Currency   = cupo.Currency,
            Amount     = utilizado,
            CupoAmount = cupo.Amount
        };

        return new EventCondition(alert, facts, previous => MetricOrSeverityUp(alert, previous));
    }

    /// <summary>
    /// PYME_REVIEW_DUE: Argentine calendar days since the PyME's latest approved analysis. 365 or more is
    /// High ("vencida"), 335 to 364 Medium ("próxima"); null under 335 — <see cref="AnnualReviewRule"/>, the
    /// rule the dashboard's annualReview reads too.
    /// </summary>
    /// <remarks>
    /// Worse, for a dismissal, means a higher severity ONLY. The day count grows every day by itself; were it
    /// to count, a dismissal would last until tomorrow.
    /// </remarks>
    private static EventCondition? EvaluateReview(
        string tenantId, Guid entityId, Guid analysisId, DateTime approvedAt, string metricKey,
        DateOnly asOf, DateTime nowUtc)
    {
        if (AnnualReviewRule.Evaluate(approvedAt, asOf) is not { } review) return null;

        var approvedOn = review.ApprovedOn;
        var ageDays    = review.AgeDays;
        var isDue      = review.IsDue;

        var description =
            $"El último análisis aprobado es del {approvedOn.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} (hace {ageDays} días). " +
            "Hacé un análisis nuevo de la PyME.";

        var alert = BuildAlert(
            tenantId, entityId, analysisId, metricKey, nowUtc,
            EventAlertTypes.PymeReviewDue,
            review.Severity,
            isDue ? "Revisión anual vencida" : "Revisión anual próxima",
            description,
            metricValue: ageDays,
            thresholdValue: AnnualReviewRule.DueDays);

        var facts = new AlertFactsDto
        {
            Date          = approvedOn,
            SecondaryDate = review.DueDate,
            DaysDelta     = review.DaysDelta
        };

        return new EventCondition(alert, facts, previous => SeverityRank(alert.Severity) > SeverityRank(previous.Severity));
    }

    /// <summary>
    /// PYME_BCRA_WORSENED: the dashboard's bcraSignals rule for this PyME — <see cref="BcraSignalRule"/>,
    /// situación now worse than at the analysis behind its cupo. High from situación 3, Medium below. Null
    /// when it is not worse. The caller has already ruled out an unreadable side.
    /// </summary>
    /// <remarks>Worse, for a dismissal, means a higher situación now (and so possibly a higher severity).</remarks>
    private static EventCondition? EvaluateBcra(
        string tenantId, BcraEntityComparison comparison, string metricKey, DateTime nowUtc)
    {
        if (!comparison.IsWorsened) return null;

        var then = comparison.Then!.Value;
        var now  = comparison.Now!.Value;

        DateOnly? queriedOn = comparison.Row.BcraDataUpdatedAt is { } queriedAt ? MaturityBands.AsOf(queriedAt) : null;

        var consulta = queriedOn is { } day
            ? $" Consulta al BCRA del {day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}."
            : string.Empty;

        var description =
            $"Pasó de situación {then} a {now} desde el análisis que respalda el cupo.{consulta} " +
            "Revisá la PyME y evaluá reanalizarla o suspender el cupo.";

        var alert = BuildAlert(
            tenantId, comparison.Row.EntityId, comparison.Row.AnalysisId, metricKey, nowUtc,
            EventAlertTypes.PymeBcraWorsened,
            now >= BcraHighSituacion ? HighSeverity : MediumSeverity,
            "Empeoró la situación en el BCRA",
            description,
            metricValue: now,
            thresholdValue: then);

        var facts = new AlertFactsDto
        {
            SituacionFrom = then,
            SituacionTo   = now,
            Date          = queriedOn
        };

        return new EventCondition(alert, facts, previous => MetricOrSeverityUp(alert, previous));
    }

    /// <summary>
    /// Whether <paramref name="op"/> was already in the Days0To30 band at <paramref name="instantUtc"/>: it
    /// existed, it was 'Vigente' (a NULL VigenteConfirmadaEl predates script 040 and is taken as "always"),
    /// and its date was in the band as of that instant.
    /// </summary>
    private static bool WasExpiringAt(CreditLineOperation op, DateTime instantUtc)
        => op.CreatedAt <= instantUtc
        && (op.VigenteConfirmadaEl is not { } confirmedAt || confirmedAt <= instantUtc)
        && MaturityBands.BucketOf(op.ValidUntil!.Value, MaturityBands.AsOf(instantUtc), instantUtc) == MaturityBands.Days0To30;

    private static bool MetricOrSeverityUp(Alert current, Alert previous)
        => current.MetricValue > previous.MetricValue
        || SeverityRank(current.Severity) > SeverityRank(previous.Severity);

    private static Alert BuildAlert(
        string   tenantId,
        Guid     entityId,
        Guid?    analysisId,
        string   metricKey,
        DateTime nowUtc,
        string   alertType,
        string   severity,
        string   title,
        string   description,
        decimal  metricValue,
        decimal  thresholdValue)
        => new()
        {
            AlertId        = Guid.NewGuid(), // AlertConfiguration maps AlertId as ValueGeneratedNever — the app supplies it.
            TenantId       = tenantId,
            EntityId       = entityId,
            AnalysisId     = analysisId,
            AlertType      = alertType,
            Severity       = severity,
            Title          = title,
            Description    = description,
            MetricKey      = metricKey,
            MetricValue    = metricValue,
            ThresholdValue = thresholdValue,
            IsRead         = false,
            IsResolved     = false,
            CreatedAt      = nowUtc
        };

    /// <summary>
    /// Copies what the situation says now onto the open alert. Returns whether anything changed.
    /// An escalation (Medium → High) makes it unread again: a review that went from coming up to overdue,
    /// or a BCRA situación that crossed into 3, is news even to someone who read the first version.
    /// </summary>
    private static bool UpdateInPlace(Alert existing, Alert current)
    {
        var escalated = SeverityRank(current.Severity) > SeverityRank(existing.Severity);

        var changed =
               !string.Equals(existing.Title,       current.Title,       StringComparison.Ordinal)
            || !string.Equals(existing.Description, current.Description, StringComparison.Ordinal)
            || !string.Equals(existing.Severity,    current.Severity,    StringComparison.Ordinal)
            || existing.MetricValue    != current.MetricValue
            || existing.ThresholdValue != current.ThresholdValue
            || existing.EntityId       != current.EntityId
            || existing.AnalysisId     != current.AnalysisId;

        if (!changed) return false;

        existing.Title          = current.Title;
        existing.Description    = current.Description;
        existing.Severity       = current.Severity;
        existing.MetricValue    = current.MetricValue;
        existing.ThresholdValue = current.ThresholdValue;
        existing.EntityId       = current.EntityId;
        existing.AnalysisId     = current.AnalysisId;

        if (escalated)
        {
            existing.IsRead = false;
            existing.ReadBy = null;
            existing.ReadAt = null;
        }

        return true;
    }

    /// <summary>
    /// Whether the most recent alert for this (type, key) was dismissed by a user and the situation has
    /// not got worse since — "worse" being each type's own predicate (see each Evaluate method). It comes
    /// back only when it does. An alert resolved by the system (ResolvedBy null) never suppresses: the
    /// system resolves when the situation cleared, so its return is news.
    /// </summary>
    private static bool IsSuppressed(Alert previous, EventCondition candidate)
    {
        if (!previous.IsResolved || previous.ResolvedBy is null) return false;

        return !candidate.WorsenedSince(previous);
    }

    private static void AutoResolve(Alert alert, DateTime nowUtc)
    {
        alert.IsResolved = true;
        alert.ResolvedAt = nowUtc;
        alert.ResolvedBy = null; // system auto-resolution (not a user action) — never suppresses a return
    }

    private static int SeverityRank(string? severity) => severity switch
    {
        "Critical" => 3,
        "High"     => 2,
        "Medium"   => 1,
        "Low"      => 0,
        _          => -1
    };

    // "ARS 1.500.000,00": the cupo's own currency code, es-AR grouping — the format
    // CreditLinesController puts in its own amount messages. One cupo never mixes currencies.
    private static string Money(string currency, decimal amount) => $"{currency} {amount.ToString("N2", EsAr)}";

    // An operation's ValidUntil is a calendar date stored as a UTC midnight; printed as that date.
    private static string Date(DateTime validUntil) => validUntil.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>
    /// A condition that holds now: the alert that describes it, the figures behind that description, and
    /// whether it is worse than what <c>previous</c> (a dismissed alert of the same type and key) described.
    /// </summary>
    private sealed record EventCondition(Alert Alert, AlertFactsDto Facts, Func<Alert, bool> WorsenedSince);
}
