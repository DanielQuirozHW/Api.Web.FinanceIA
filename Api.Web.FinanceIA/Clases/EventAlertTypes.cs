// Project : Api.Web.FinanceIA
// Created : 2026-10-25

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// The alert types that describe a live situation on a cupo or on the PyME behind it, rather than a
/// finding of an analysis.
/// <para>
/// One place for both readers: <see cref="CupoAlertService"/>, which raises, updates and auto-resolves
/// them, and <c>AlertRepository.SupersedePreviousEntityAlertsAsync</c>, which must leave them alone. A
/// re-analysis supersedes the previous analysis's balance findings; it does not cancel an expired
/// operation, free an exceeded cupo or improve a BCRA situación, so resolving these on that path would
/// hide a situation that is still true until the next GET /api/alerts recreated it. A new APPROVED
/// analysis does end a PYME_REVIEW_DUE — and the next reconcile pass is what resolves it.
/// </para>
/// </summary>
public static class EventAlertTypes
{
    /// <summary>An Active cupo has one or more effective-'Vencida' operations still occupying it.</summary>
    public const string CupoOperationsExpired = "CUPO_OPERATIONS_EXPIRED";

    /// <summary>
    /// An Active cupo is EXCEEDED: Utilizado above its Amount (Disponible strictly negative). The code
    /// keeps its original name; a cupo at exactly 100% is the normal state and does not alert.
    /// </summary>
    public const string CupoUtilizationHigh = "CUPO_UTILIZATION_HIGH";

    /// <summary>
    /// An Active cupo has 'Vigente' operations due within the next 30 Argentine calendar days — the
    /// dashboard's Days0To30 maturity band (<see cref="MaturityBands"/>).
    /// </summary>
    public const string CupoOperationsExpiring = "CUPO_OPERATIONS_EXPIRING";

    /// <summary>A PyME with an Active cupo has gone 335 days or more without a newly approved analysis.</summary>
    public const string PymeReviewDue = "PYME_REVIEW_DUE";

    /// <summary>
    /// A PyME with an Active cupo is in a worse BCRA situación than at the analysis behind its cupo — the
    /// dashboard's bcraSignals rule (<see cref="BcraSignalRule"/>).
    /// </summary>
    public const string PymeBcraWorsened = "PYME_BCRA_WORSENED";

    /// <summary>
    /// The event types keyed by cupo (<see cref="CupoMetricKey"/>). An array, not a set, so EF Core
    /// translates <c>Contains</c> into a SQL IN / NOT IN — the same reason
    /// <c>CreditLineRepository.NonTerminalStatuses</c> is one.
    /// </summary>
    internal static readonly string[] Cupo = { CupoOperationsExpired, CupoUtilizationHigh, CupoOperationsExpiring };

    /// <summary>The event types keyed by PyME (<see cref="EntityMetricKey"/>).</summary>
    internal static readonly string[] Pyme = { PymeReviewDue, PymeBcraWorsened };

    /// <summary>
    /// EVERY event type: what one reconcile pass owns, and what a re-analysis supersede leaves alone.
    /// A new event type joins <see cref="Cupo"/> or <see cref="Pyme"/> and therefore this.
    /// </summary>
    internal static readonly string[] All = Cupo.Concat(Pyme).ToArray();

    /// <summary>Prefix of <see cref="CupoMetricKey"/>.</summary>
    public const string CupoMetricKeyPrefix = "cupo:";

    /// <summary>Prefix of <see cref="EntityMetricKey"/>.</summary>
    public const string EntityMetricKeyPrefix = "entity:";

    /// <summary>
    /// The MetricKey that identifies which cupo a cupo alert is about: <c>cupo:{CreditLineId}</c>, 41
    /// characters against the column's nvarchar(50). One OPEN alert exists per (TenantId, AlertType,
    /// MetricKey); there is no unique index behind that, <see cref="CupoAlertService"/> keeps it.
    /// </summary>
    public static string CupoMetricKey(Guid creditLineId) => CupoMetricKeyPrefix + creditLineId.ToString("D");

    /// <summary>Reads the CreditLineId back out of a <see cref="CupoMetricKey"/>.</summary>
    public static bool TryParseCupoMetricKey(string? metricKey, out Guid creditLineId)
        => TryParse(metricKey, CupoMetricKeyPrefix, out creditLineId);

    /// <summary>
    /// The MetricKey that identifies which PyME a PyME alert is about: <c>entity:{EntityId}</c>, 43
    /// characters against the column's nvarchar(50). Same one-open-alert identity as <see cref="CupoMetricKey"/>.
    /// </summary>
    public static string EntityMetricKey(Guid entityId) => EntityMetricKeyPrefix + entityId.ToString("D");

    /// <summary>Reads the EntityId back out of an <see cref="EntityMetricKey"/>.</summary>
    public static bool TryParseEntityMetricKey(string? metricKey, out Guid entityId)
        => TryParse(metricKey, EntityMetricKeyPrefix, out entityId);

    private static bool TryParse(string? metricKey, string prefix, out Guid id)
    {
        id = Guid.Empty;
        return metricKey is not null
            && metricKey.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(metricKey[prefix.Length..], "D", out id);
    }
}
