// Project : Api.Web.FinanceIA
// Created : 2026-09-10

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Emits the per-user notifications that carry an analysis decision to whoever needs to act on it.
/// </summary>
/// <remarks>
/// The two-step decision flow can escalate an analysis but has no way to tell anyone. This is the
/// hand-off: after a decision is persisted and audited, the person on the other side of it gets a
/// notification in their inbox.
///
/// <para>
/// STRICTLY SECONDARY. The decision is the deliverable; the notification is a delivery convenience
/// on top of it. The implementation owns its own try/catch and never throws, so a notification
/// failure can never cost an analyst the decision they just made.
/// </para>
/// </remarks>
public interface IAnalysisDecisionNotifier
{
    /// <summary>
    /// Works out who should hear about one decision transition and writes their notifications.
    /// </summary>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="analysis">The analysis as persisted by the decision, post-write.</param>
    /// <param name="currentUser">The user who made the decision.</param>
    /// <param name="auditAction">
    /// The audit action name the decision resolved to — the same value written to the audit trail.
    /// This is what selects the routing case, because it is the one value that already distinguishes
    /// all five outcomes.
    /// </param>
    /// <param name="wasPreApproved">
    /// Whether the analysis was in 'PreApproved' state BEFORE this decision. Splits the two cases
    /// that share the 'Analysis.Rejected' action: rejecting a pre-approval (tell the analyst who
    /// proposed it) versus rejecting outright (tell whoever requested the analysis).
    /// </param>
    /// <param name="entityName">Business name of the analysed company, for the copy. May be null.</param>
    /// <param name="amount">The decided amount, for the copy. May be null on a rejection or return.</param>
    /// <param name="currency">The decided currency, for the copy. May be empty.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task NotifyAsync(
        string            tenantId,
        Analysis          analysis,
        User              currentUser,
        string            auditAction,
        bool              wasPreApproved,
        string?           entityName,
        decimal?          amount,
        string?           currency,
        CancellationToken ct);
}
