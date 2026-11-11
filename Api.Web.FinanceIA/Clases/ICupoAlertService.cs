// Project : Api.Web.FinanceIA
// Created : 2026-10-25

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Keeps the tenant's event alerts (<see cref="EventAlertTypes.All"/>) in step with its Active cupos and
/// the PyMEs that hold them: raises them when a situation needs acting on, updates them while it lasts,
/// and resolves them when it is over. If nothing changed or nothing has to be done, it is not an alert.
/// <para>
/// Named for the cupo alerts it started with; it now also owns the PyME-level ones (PYME_REVIEW_DUE,
/// PYME_BCRA_WORSENED), all in the same pass, under the same gate, in one SaveChanges — and closes the
/// billing plan alerts (<see cref="BillingAlertService.AllTypes"/>) left open from an earlier billing period.
/// </para>
/// </summary>
public interface ICupoAlertService
{
    /// <summary>
    /// Reconciles every event alert of <paramref name="tenantId"/> against its Active cupos and their
    /// PyMEs as of <paramref name="nowUtc"/>.
    /// </summary>
    /// <param name="tenantId">Tenant scope. Always enforced on every read and write.</param>
    /// <param name="nowUtc">
    /// The one instant the whole pass is decided on — whether an operation is 'Vencida' is
    /// <c>CreditLineRules.EffectiveStatus(op, nowUtc)</c>, exactly as the cupo card and the dashboard
    /// decide it, and the Argentine date it falls on is the day "por vencer" and the annual review count from.
    /// </param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <remarks>
    /// NEVER THROWS, except <see cref="OperationCanceledException"/> when <paramref name="ct"/> itself
    /// was cancelled. Every other failure is logged as a Warning and swallowed: the caller is the alerts
    /// listing, which must not acquire a new failure mode by asking for this.
    /// </remarks>
    /// <returns>
    /// The facts (<see cref="AlertFactsDto"/>) of every alert the pass left OPEN with a condition it
    /// evaluated, keyed by AlertId — what the listing attaches to its rows. Null when no pass ran: the gate
    /// timed out, or the pass failed. A key the pass left untouched (nothing to evaluate it on) has no entry.
    /// </returns>
    Task<IReadOnlyDictionary<Guid, AlertFactsDto>?> ReconcileAsync(string tenantId, DateTime nowUtc, CancellationToken ct);
}
