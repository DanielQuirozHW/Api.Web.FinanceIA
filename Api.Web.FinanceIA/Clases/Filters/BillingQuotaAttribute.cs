// Project : Api.Web.FinanceIA
// Created : 2026-08-31

using Microsoft.AspNetCore.Mvc;

namespace Api.Web.FinanceIA.Clases.Filters;

/// <summary>
/// Which monthly plan limit an endpoint consumes.
/// </summary>
public enum BillingQuotaKind
{
    /// <summary>Counts against <c>BillingPlan.AnalysisMonthlyLimit</c>. Blocks unconditionally.</summary>
    Analysis,

    /// <summary>
    /// Counts against <c>BillingPlan.ChatTokensMonthlyLimit</c>. Blocks only when
    /// <c>Billing:EnforceChatTokens</c> is on; otherwise measured and logged.
    /// </summary>
    ChatTokens
}

/// <summary>
/// Refuses a request that would exceed the tenant's monthly plan limit.
///
/// <para>
/// Deliberately an action filter and not a check inside the action. The gate has to run before
/// anything the action does that costs money or moves state — before a document flips to
/// "Processing", before an analyst's chat turn is persisted, and above all before a Claude API
/// call, since refusing a request only after paying for it is not refusing it. Expressing that as
/// "the first lines of the method" makes the ordering a convention that the next edit can quietly
/// break; expressing it as a filter makes it structural.
/// </para>
///
/// <para>
/// Applied to <c>AnalysisController.TriggerAsync</c> and <c>AnalysisController.ChatAsync</c>.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class BillingQuotaAttribute : TypeFilterAttribute
{
    /// <summary>
    /// Gates the decorated action on <paramref name="kind"/>.
    /// </summary>
    public BillingQuotaAttribute(BillingQuotaKind kind)
        : base(typeof(BillingQuotaFilter))
    {
        Arguments = [kind];
    }
}
