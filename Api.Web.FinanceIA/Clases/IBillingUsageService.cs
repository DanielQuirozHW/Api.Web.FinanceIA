// Project : Api.Web.FinanceIA
// Created : 2026-08-31

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Composes a tenant's plan limits with what it has actually consumed this calendar month.
///
/// <para>
/// The single source of truth for both the enforcement gate and the usage endpoint, so the
/// figures that block and the figures the Portal renders cannot drift apart.
/// </para>
/// </summary>
public interface IBillingUsageService
{
    /// <summary>
    /// Returns the tenant's usage against its plan for the current UTC calendar month.
    ///
    /// <para>
    /// Does not throw for a failed read. Each of the three inputs — the plan, the analysis count,
    /// the token sum — is read independently and its failure reported on the snapshot rather than
    /// raised, so one broken table degrades one number instead of the endpoint and both gates.
    /// Callers ask <c>AnalysisQuotaIsUnreliable</c> / <c>ChatQuotaIsUnreliable</c> and fail open
    /// on them; the endpoint reports <c>DegradedReason</c> rather than going blank.
    /// </para>
    /// </summary>
    Task<BillingUsageSnapshot> GetSnapshotAsync(string tenantId, CancellationToken ct);
}
