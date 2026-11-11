// Project : Api.Web.FinanceIA
// Created : 2026-06-06

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Persistence operations for <see cref="Alert"/> records
/// in the per-tenant business database.
/// </summary>
public interface IAlertRepository
{
    Task CreateManyAsync(IEnumerable<Alert> alerts, CancellationToken ct);
    Task<IReadOnlyList<Alert>> GetAllByTenantAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns all alerts for the given entity within the given tenant, ordered by CreatedAt DESC.
    /// </summary>
    Task<IReadOnlyList<Alert>> GetByEntityIdAsync(Guid entityId, string tenantId, CancellationToken ct);
    Task<Alert?> GetByIdAsync(Guid alertId, string tenantId, CancellationToken ct);
    Task<bool> MarkAsReadAsync(Guid alertId, string tenantId, Guid userId, CancellationToken ct);
    Task<bool> MarkAsResolvedAsync(Guid alertId, string tenantId, Guid userId, CancellationToken ct);

    /// <summary>
    /// Auto-resolves (supersedes) all still-active alerts of a given entity, so that a
    /// freshly-analyzed entity does not accumulate duplicate alerts across analyses.
    /// Only alerts with IsResolved == false are touched; manually-resolved alerts keep
    /// their original ResolvedAt/ResolvedBy. System auto-resolution leaves ResolvedBy null.
    /// The event alerts (<see cref="EventAlertTypes.All"/>, cupo and PyME) are never touched: they
    /// describe a live situation of the entity's cupo or of the PyME, not the previous analysis, and
    /// <see cref="CupoAlertService"/> owns their lifecycle.
    /// </summary>
    /// <param name="entityId">Entity whose previous active alerts should be superseded.</param>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="excludeAnalysisId">
    /// When set, alerts belonging to this analysis are NOT superseded (safety guard so the
    /// new analysis's own alerts are never resolved if they already exist).
    /// </param>
    /// <returns>The number of alerts that were superseded.</returns>
    Task<int> SupersedePreviousEntityAlertsAsync(
        Guid entityId, string tenantId, Guid? excludeAnalysisId, CancellationToken ct);

    /// <summary>
    /// Marks every unread alert for the given tenant as read.
    /// </summary>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="userId">User performing the action.</param>
    /// <returns>The number of alerts that were marked as read.</returns>
    Task<int> MarkAllAsReadAsync(string tenantId, Guid userId, CancellationToken ct);

    /// <summary>
    /// Whether an alert of the given type already exists for the tenant within the given period.
    /// </summary>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="alertType">Exact <see cref="Alert.AlertType"/> code to look for.</param>
    /// <param name="periodStartUtc">Start of the window, UTC, INCLUSIVE.</param>
    /// <param name="periodEndUtc">End of the window, UTC, EXCLUSIVE.</param>
    /// <remarks>
    /// The dedup primitive behind the billing quota alerts. It exists so a recurring emitter can ask
    /// "did this already fire this month?" without loading the tenant's alert history on every poll,
    /// which is what <see cref="GetAllByTenantAsync"/> would cost.
    ///
    /// <para>
    /// The window is half-open on purpose so it composes with BillingUsageSnapshot.PeriodStart /
    /// PeriodEnd, which are the 1st of this month and the 1st of the next: an alert written in the
    /// first instant of a month belongs to that month, and one written in the first instant of the
    /// next belongs to the next. A closed interval would let a boundary alert satisfy two periods.
    /// </para>
    /// </remarks>
    Task<bool> ExistsByTypeInPeriodAsync(
        string tenantId,
        string alertType,
        DateTime periodStartUtc,
        DateTime periodEndUtc,
        CancellationToken ct);

    /// <summary>
    /// The tenant's OPEN alerts (IsResolved == false) of the given types, TRACKED, so the caller can
    /// update or resolve them in place and persist through <see cref="SaveReconciliationAsync"/>.
    /// </summary>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="alertTypes">Exact <see cref="Alert.AlertType"/> codes to load.</param>
    Task<IReadOnlyList<Alert>> GetOpenByTypesForUpdateAsync(
        string tenantId, IReadOnlyCollection<string> alertTypes, CancellationToken ct);

    /// <summary>
    /// The tenant's OPEN alerts (IsResolved == false) of the given types created strictly before
    /// <paramref name="createdBeforeUtc"/>, TRACKED, so the caller can resolve them in place and persist
    /// through <see cref="SaveReconciliationAsync"/>. One statement.
    /// </summary>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="alertTypes">Exact <see cref="Alert.AlertType"/> codes to load.</param>
    /// <param name="createdBeforeUtc">
    /// UTC, EXCLUSIVE — the start of a half-open period, so an alert written at its first instant
    /// belongs to that period and is not returned.
    /// </param>
    Task<IReadOnlyList<Alert>> GetOpenByTypesCreatedBeforeForUpdateAsync(
        string tenantId, IReadOnlyCollection<string> alertTypes, DateTime createdBeforeUtc, CancellationToken ct);

    /// <summary>
    /// The most recent alert (by CreatedAt) of each (AlertType, MetricKey) pair among
    /// <paramref name="metricKeys"/>, open or resolved, keyed by that pair. Pairs with no alert at all
    /// are absent. Read-only.
    /// </summary>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="alertTypes">Exact <see cref="Alert.AlertType"/> codes to consider.</param>
    /// <param name="metricKeys">Exact <see cref="Alert.MetricKey"/> values to consider.</param>
    Task<IReadOnlyDictionary<(string AlertType, string MetricKey), Alert>> GetLatestByMetricKeysAsync(
        string tenantId,
        IReadOnlyCollection<string> alertTypes,
        IReadOnlyCollection<string> metricKeys,
        CancellationToken ct);

    /// <summary>
    /// Persists one reconciliation pass in a single SaveChanges: inserts <paramref name="toCreate"/> and
    /// writes whatever the caller changed on the alerts it loaded through
    /// <see cref="GetOpenByTypesForUpdateAsync"/> and <see cref="GetOpenByTypesCreatedBeforeForUpdateAsync"/>.
    /// </summary>
    /// <param name="tenantId">Tenant scope. Every alert in <paramref name="toCreate"/> must carry it.</param>
    /// <param name="toCreate">New alerts; may be empty.</param>
    /// <exception cref="ArgumentException">A new alert belongs to another tenant.</exception>
    Task SaveReconciliationAsync(string tenantId, IReadOnlyCollection<Alert> toCreate, CancellationToken ct);

    /// <summary>
    /// The RESOLVED event alerts (<see cref="EventAlertTypes.All"/>) of the tenant whose MetricKey is one of
    /// <paramref name="metricKeys"/>, projected — no Title or Description. Read-only, one statement. The
    /// input of <c>reopenedAfter</c> on the alerts listing.
    /// </summary>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="metricKeys">Exact MetricKey values of the open event alerts being listed.</param>
    Task<IReadOnlyList<PreviousAlertRow>> GetPreviousByKeysAsync(
        string tenantId, IReadOnlyCollection<string> metricKeys, CancellationToken ct);

    /// <summary>
    /// Reopens an alert a USER resolved: IsResolved = false, ResolvedAt and ResolvedBy cleared. Refused when
    /// the system resolved it, or when another OPEN alert of the tenant already exists for the same
    /// (AlertType, MetricKey, EntityId). An alert that is already open is left as it is and reported as
    /// <see cref="AlertUnresolveOutcome.Reopened"/>.
    /// </summary>
    /// <param name="alertId">The alert.</param>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    Task<AlertUnresolveOutcome> UnresolveAsync(Guid alertId, string tenantId, CancellationToken ct);
}
