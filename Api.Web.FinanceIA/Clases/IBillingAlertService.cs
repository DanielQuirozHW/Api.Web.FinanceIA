// Project : Api.Web.FinanceIA
// Created : 2026-08-31

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Emits the plan-consumption alerts (80% and 100%) that land in the tenant's alerts inbox.
///
/// <para>
/// Separate from <see cref="IBillingUsageService"/> deliberately: that one MEASURES and is called by
/// the enforcement gate on a path where a write would be wrong, this one WRITES. Keeping the two
/// apart is what lets the quota filter keep calling the measurement without ever emitting an alert
/// from a request that is about to be refused.
/// </para>
/// </summary>
public interface IBillingAlertService
{
    /// <summary>
    /// Emits whichever threshold alerts are due for this tenant and period, and no others.
    /// </summary>
    /// <param name="tenantId">Tenant scope. Always enforced on both the read and the write.</param>
    /// <param name="snapshot">
    /// The usage already computed for this request. The percentages are taken from here and never
    /// recomputed, so what the Portal renders and what the alert says are the one number.
    /// </param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <remarks>
    /// NEVER THROWS. Every failure is caught, logged and swallowed. Callers sit on read paths that
    /// must not acquire a new failure mode by asking for this, so there is nothing to catch and
    /// nothing to await beyond the call itself.
    /// </remarks>
    Task EvaluateAsync(string tenantId, BillingUsageSnapshot snapshot, CancellationToken ct);
}
