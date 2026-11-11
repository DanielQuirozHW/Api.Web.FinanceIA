// Project : Api.Web.FinanceIA
// Created : 2026-08-31

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Resolves the EFFECTIVE billing configuration for a tenant: the stored row when there is an
/// active one, otherwise the in-code minimum plan.
///
/// <para>
/// The read and the write have deliberately OPPOSITE failure contracts. The read never throws,
/// because enforcement must fail open. The write does throw, because a SuperAdmin who is told a
/// plan was saved must not be told that by a write that failed.
/// </para>
/// </summary>
public interface ITenantBillingConfigService
{
    /// <summary>
    /// Returns the limits in force for <paramref name="tenantId"/>, together with how they were
    /// obtained.
    ///
    /// <para>
    /// NEVER returns null and NEVER throws: a tenant with no row, a tenant whose row is
    /// inactive, and a failed read all resolve to a non-null plan. They are NOT interchangeable
    /// though — a failed read comes back as <see cref="BillingPlanSource.ReadFailed"/>, and any
    /// caller that gates a request on these numbers must let the request through in that case.
    /// See <see cref="BillingPlanResolution.IsDegraded"/>.
    /// </para>
    /// </summary>
    Task<BillingPlanResolution> GetEffectiveAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Sets <paramref name="tenantId"/>'s billing configuration to <paramref name="plan"/> and
    /// returns the persisted row. Idempotent: one row per tenant, so applying the same values
    /// twice leaves the same state.
    ///
    /// <para>
    /// PROPAGATES failures, unlike <see cref="GetEffectiveAsync"/>. Fail-open is an enforcement
    /// policy for the read path — refusing a paying customer's work over a database blip is worse
    /// than over-serving. It is not a write policy: a swallowed write would report success for a
    /// plan change that never happened.
    /// </para>
    /// </summary>
    /// <param name="tenantId">Tenant whose configuration is being set.</param>
    /// <param name="plan">Label and the two monthly limits to store.</param>
    /// <param name="isActive">False switches the row off without deleting it.</param>
    /// <param name="updatedBy">Id of the SuperAdmin making the change, for audit.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<TenantBillingConfig> UpdateAsync(
        string tenantId,
        BillingPlan plan,
        bool isActive,
        string updatedBy,
        CancellationToken ct);
}
