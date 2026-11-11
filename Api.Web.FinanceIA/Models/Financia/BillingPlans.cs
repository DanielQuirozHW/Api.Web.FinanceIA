// Project : Api.Web.FinanceIA
// Created : 2026-08-31

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The monthly limits in force for one tenant: the effective billing configuration.
/// Returned by <c>ITenantBillingConfigService.GetEffectiveAsync</c>, built either from a
/// <see cref="TenantBillingConfig"/> row or from <see cref="BillingPlans.Minimum"/> when the
/// tenant has none.
/// </summary>
/// <param name="PlanName">Human-readable label for the plan, e.g. <c>"100 análisis"</c>.</param>
/// <param name="AnalysisMonthlyLimit">Analyses allowed per calendar month.</param>
/// <param name="ChatTokensMonthlyLimit">Claude chat tokens allowed per calendar month.</param>
/// <param name="ChatContextBudgetTokens">
/// Claude tokens a SINGLE chat conversation's context may occupy. Orthogonal to
/// <paramref name="ChatTokensMonthlyLimit"/>, which is a tenant-wide monthly pool: this one bounds
/// how large one prompt may get, and nothing consumes it. Not enforced yet.
/// </param>
/// <param name="ChatEnabledModels">
/// Ids of the Claude chat models the tenant may use, ordered by <see cref="ChatModelCatalog.All"/>
/// and never empty.
/// <para>
/// The odd one out on this record: the three parameters above are QUANTITIES that get consumed, and
/// exceeding one refuses a request. This is a CAPABILITY SET — which models the analyst may pick
/// between — and using it does not spend it. It rides along because it is resolved from the same row
/// and the same fallback, so a caller holding the plan holds the whole entitlement.
/// </para>
/// <para>
/// The FIRST entry is load-bearing: it is the model a chat turn uses when the analyst named none, or
/// named one this tenant is not entitled to.
/// </para>
/// </param>
public sealed record BillingPlan(
    string PlanName,
    int AnalysisMonthlyLimit,
    long ChatTokensMonthlyLimit,
    int ChatContextBudgetTokens,
    IReadOnlyList<string> ChatEnabledModels);

/// <summary>
/// Where the limits in a <see cref="BillingPlanResolution"/> came from.
///
/// <para>
/// The distinction exists so enforcement can tell a tenant that is genuinely on the minimum
/// plan from one whose real plan could not be read. Both carry the same numbers; only one of
/// them is a fact.
/// </para>
/// </summary>
public enum BillingPlanSource
{
    /// <summary>An active <see cref="TenantBillingConfig"/> row supplied the limits.</summary>
    Configured,

    /// <summary>
    /// The tenant has no active row, so <see cref="BillingPlans.Minimum"/> applies. This is the
    /// expected state while billing rolls out, and it is a real answer: the tenant IS on the
    /// minimum plan.
    /// </summary>
    NoConfiguration,

    /// <summary>
    /// The row could not be read — a transient database fault, not a commercial fact. The
    /// limits are a fallback and must NOT be enforced against. See
    /// <see cref="BillingPlanResolution.IsDegraded"/>.
    /// </summary>
    ReadFailed
}

/// <summary>
/// The effective plan for one tenant, together with how it was arrived at.
///
/// <para>
/// Phase 1 returned a bare <see cref="BillingPlan"/> and could not express the difference between
/// "this tenant is on the 50 plan" and "we could not find out, so here is the 50 plan". That was
/// harmless while nothing enforced the numbers. It is not harmless now: without this
/// discriminator, one failed SELECT would demote a 200-plan tenant to 50 and start refusing its
/// analyses.
/// </para>
/// </summary>
/// <param name="Plan">The limits in force, never null.</param>
/// <param name="Source">How <paramref name="Plan"/> was obtained.</param>
public sealed record BillingPlanResolution(BillingPlan Plan, BillingPlanSource Source)
{
    /// <summary>
    /// True when the stored configuration could not be read, so <see cref="Plan"/> is a fallback
    /// rather than the tenant's real entitlement.
    ///
    /// <para>
    /// ENFORCEMENT MUST FAIL OPEN ON THIS. Blocking a paying customer because a database read
    /// blipped is a worse failure than letting a request through over quota.
    /// </para>
    /// </summary>
    public bool IsDegraded => Source == BillingPlanSource.ReadFailed;
}

/// <summary>
/// Single source of truth for the commercial billing presets and for the fallback a tenant
/// gets when it has no <see cref="TenantBillingConfig"/> row.
///
/// <para>
/// The presets deliberately live here rather than in a database catalogue table, for the same
/// reason <see cref="TenantSettingKeys"/> holds the tenant-settings defaults: a tenant with no
/// row must resolve to something sensible and must never fail, and adding a preset must not
/// require inserting a row for every existing tenant.
/// </para>
///
/// <para>
/// These presets are NOT consulted to interpret an existing row. A tenant that has a row gets
/// the limits stored on that row, and <see cref="TenantBillingConfig.PlanName"/> is only a
/// label — so a tenant can be priced off-preset without a code change. The presets supply the
/// no-row default and a convenient set of numbers when seeding.
/// </para>
/// </summary>
public static class BillingPlans
{
    // ── Commercial presets ──────────────────────────────────────────────────────
    //
    // The análisis counts (50 / 100 / 200) are the agreed commercial tiers.
    //
    // TODO(billing): confirm chat-token limits per plan with product. The three token
    // figures below are PLACEHOLDERS chosen to scale linearly with the análisis count so the
    // relative shape is right; none of them has been agreed. They are STILL not agreed, which is
    // exactly why the chat gate ships measured-but-not-blocking behind Billing:EnforceChatTokens
    // (default false). Settle these numbers, and verify [dbo].[ApiCallLogs] is actually being
    // written (see script 043), BEFORE that switch is turned on.
    //
    // TODO(billing): the same is true of the three CONTEXT BUDGETS (40k / 60k / 80k), added by
    // script 051 and equally unagreed. They are a different KIND of number from the two above — a
    // ceiling on how large ONE conversation's prompt may get, not a monthly pool anything spends —
    // but they scale with the tier for the same commercial reason: a larger plan buys a longer
    // conversation. They are shaped against the chat model's 200.000-token window, leaving room for
    // the system prompt, an attachment and the answer.
    //
    // They are now READ. AnalysisController.ChatAsync gates the next turn of a conversation whose
    // last assistant turn was already billed at or above the budget, and answers 429 with errorCode
    // CHAT_CONTEXT_BUDGET_EXCEEDED. It ships measured-but-not-blocking behind
    // Billing:EnforceChatContextBudget (default false), for the same reason as the token limits
    // above: these three figures are still unagreed. While the switch is off the gate logs a Warning
    // naming the tenant, plan, conversation, observed size and budget every time it WOULD have
    // blocked. Those logs are the real transcript sizes this TODO was waiting for — read them before
    // settling the numbers or turning the switch on.
    //
    // THE ATTACHMENT CAVEAT — now ADDRESSED, and worth reading before trusting the logs. The gate
    // reads ChatMessages.InputTokens, which is Anthropic's count for the WHOLE request — including
    // the chat attachment, which is ephemeral and never replayed. The attachment cap is 200.000 chars
    // (~67k tokens) against a 40.000 minimum budget, so a single attachment turn used to leave a
    // conversation measured far above the transcript it will actually replay, and pin it there.
    // Script 053 added ChatMessages.HadEphemeralAttachment; the assistant row records it, and
    // AnalysisController.LastAssistantInputTokens now SKIPS those rows and measures by the most
    // recent clean turn instead, so an attachment turn no longer pins anything.
    //
    // It is a fix in the safe direction, not a reconstruction. The skipped turn's history-only count
    // is not recovered — it was never computed — so a conversation measures by an OLDER clean turn
    // and therefore UNDER-reports, by more the longer the analyst keeps attaching. A conversation
    // whose only assistant turn carried an attachment measures as null and is not gated at all. Two
    // consequences for reading these logs: they are a LOWER bound on real transcript sizes, and the
    // flag is forward-only — a conversation that took an attachment turn before script 053 deployed
    // still carries its inflated figure and is still measured by it.
    //
    // A failed trigger is still free, and deliberately so: the [dbo].[Analyses] row that the
    // analysis quota counts is written only after Claude answers, so a document that fails
    // extraction is never billed as one of these análisis. What used to make that a leak — a
    // tenant at 49/50 able to spend unbounded Claude money retrying the same unreadable PDF
    // against one free slot — is now bounded elsewhere, by a per-FILE extraction budget
    // (Documents.ExtractionAttempts summed by FileHashSHA256, capped by
    // Billing:MaxExtractionAttemptsPerDocument and refused in ReanalysisPolicy before the Claude
    // call). The counts here are unchanged: a failure still consumes no análisis, and inserting
    // an Analyses row at trigger time — which the terminal-state guard, ReanalysisPolicy and two
    // NOT NULL columns unknown at trigger time all have opinions about — remains something this
    // codebase deliberately does not do. See IAnalysisRepository.CountByTenantInPeriodAsync.

    // The chat MODEL LIST is deliberately NOT tiered, and is the same on all three presets. Unlike
    // the figures above it, it is not a quantity anyone can overspend — it is which models the
    // analyst's picker offers — and it was hardcoded ON for every tenant before script 052 gave it a
    // column, so tiering it here would take a working capability away from the two smaller plans as
    // a side effect of this delivery. Whether model access SHOULD become a commercial lever ("Sonnet
    // 5 only on the 200 plan") is a product decision, not a default; until it is made, the presets
    // supply the whole catalogue and a SuperAdmin narrows an individual tenant through
    // PUT /api/billing/config. Note that ChatModelCatalog.AllIds is a ceiling as well as a default:
    // a tenant's list is filtered against it on read, so retiring a model narrows every plan at once.

    /// <summary>Entry tier: 50 analyses per month. Also the fallback — see <see cref="Minimum"/>.</summary>
    public static readonly BillingPlan Plan50 =
        new("50 análisis", 50, 2_000_000L, 40_000, ChatModelCatalog.AllIds);

    /// <summary>Mid tier: 100 analyses per month.</summary>
    public static readonly BillingPlan Plan100 =
        new("100 análisis", 100, 4_000_000L, 60_000, ChatModelCatalog.AllIds);

    /// <summary>Top tier: 200 analyses per month.</summary>
    public static readonly BillingPlan Plan200 =
        new("200 análisis", 200, 8_000_000L, 80_000, ChatModelCatalog.AllIds);

    /// <summary>
    /// The minimum plan, and the single source of truth for the effective configuration of a
    /// tenant that has no row — or whose row is inactive. Deliberately the smallest preset:
    /// an unconfigured tenant should get the least, never the most.
    /// </summary>
    public static readonly BillingPlan Minimum = Plan50;

    // ── Lookup ──────────────────────────────────────────────────────────────────

    /// <summary>All presets, in ascending order of size.</summary>
    public static readonly IReadOnlyList<BillingPlan> All = [Plan50, Plan100, Plan200];

    /// <summary>
    /// Finds a preset by its <see cref="BillingPlan.PlanName"/>, case-insensitively, or null
    /// when no preset carries that name. Provided for seeding and for a future admin UI —
    /// the read path does NOT use this to interpret a stored row.
    /// </summary>
    public static BillingPlan? Find(string? planName)
    {
        if (string.IsNullOrWhiteSpace(planName))
            return null;

        var trimmed = planName.Trim();

        foreach (var plan in All)
        {
            if (string.Equals(plan.PlanName, trimmed, StringComparison.OrdinalIgnoreCase))
                return plan;
        }

        return null;
    }
}
