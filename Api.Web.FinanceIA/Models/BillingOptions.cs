// Project : Api.Web.FinanceIA
// Created : 2026-08-31

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// Billing enforcement switches, bound from the "Billing" configuration section.
/// </summary>
public sealed record BillingOptions
{
    /// <summary>
    /// Whether exceeding <c>ChatTokensMonthlyLimit</c> actually rejects a chat turn.
    ///
    /// <para>
    /// Default false, and deliberately so. The per-plan token figures in
    /// <see cref="Financia.BillingPlans"/> are placeholders that product has not agreed, and the
    /// ledger they are measured against — [dbo].[ApiCallLogs] — has its own open question (see
    /// script 043 in docs/database). Blocking an analyst's question on an un-agreed number read
    /// from a table that may not be recording is not a defensible thing to ship.
    /// </para>
    ///
    /// <para>
    /// While false the gate still runs, still computes the same figures the usage endpoint
    /// reports, and logs a Warning naming them whenever it WOULD have blocked. That log is the
    /// evidence for deciding the real limits. Flipping this to true is then a configuration
    /// change, not a code change.
    /// </para>
    ///
    /// <para>
    /// The analysis limit has no such switch: those tiers (50/100/200) are the agreed commercial
    /// numbers and it blocks unconditionally.
    /// </para>
    /// </summary>
    public bool EnforceChatTokens { get; init; }

    /// <summary>
    /// Whether exceeding the plan's <c>ChatContextBudgetTokens</c> actually rejects the next turn
    /// of a conversation that has already outgrown it.
    ///
    /// <para>
    /// Default false, for the same reason as <see cref="EnforceChatTokens"/> and no other: the three
    /// per-plan context budgets (40k / 60k / 80k) are placeholders product has not agreed. See the
    /// TODO in <see cref="Financia.BillingPlans"/>. Refusing an analyst's question against a number
    /// nobody signed off on is not a defensible thing to ship, however sound the mechanism is.
    /// </para>
    ///
    /// <para>
    /// While false the gate still runs and still measures. Whenever it WOULD have blocked it logs a
    /// Warning naming the tenant, the plan, the conversation, the observed context size and the
    /// budget. Those logs are the evidence for settling the real figures, and they are the only way
    /// to find out how often the caveat below actually bites. Flipping this to true is then a
    /// configuration change, not a code change.
    /// </para>
    ///
    /// <para>
    /// FORMER CAVEAT, now addressed — and what replaced it still shapes how these logs read. The gate
    /// reads the input_tokens Anthropic billed on the last assistant turn, and that figure covers the
    /// WHOLE request — including a chat attachment, which is never replayed: it stays out of
    /// <c>Content</c>, which is what history is rebuilt from, whatever else stores it.
    /// The attachment cap is 200.000 characters against a minimum budget of 40.000 tokens,
    /// so a single attachment turn used to leave a conversation measured far above a transcript that
    /// is actually tiny, and pin it there. Script 053 added
    /// <see cref="Financia.ChatMessage.HadEphemeralAttachment"/>, and
    /// <c>AnalysisController.LastAssistantInputTokens</c> skips the turns that carry it, measuring by
    /// the most recent clean turn instead.
    /// </para>
    ///
    /// <para>
    /// What that leaves is an UNDER-report rather than an over-report: the skipped turn's
    /// history-only count is not reconstructed (it was never computed), so the measurement falls back
    /// to an older and smaller clean turn, and a conversation whose only assistant turn carried an
    /// attachment is not gated at all. Both are the fail-open direction this gate is built to be
    /// wrong in. The flag is also forward-only — a conversation that took an attachment turn before
    /// script 053 deployed keeps its inflated figure — so the Warnings above are a LOWER bound on
    /// real transcript sizes, with a residue of pre-053 conversations reading high.
    /// </para>
    /// </summary>
    public bool EnforceChatContextBudget { get; init; }

    /// <summary>
    /// How many failed extraction attempts a single balance file may cost before
    /// <c>POST /api/analyses</c> refuses to run another one. Default 2.
    ///
    /// <para>
    /// This is a SPEND limit, not a quota. A failed extraction never consumes one of the tenant's
    /// monthly análisis — the <c>[dbo].[Analyses]</c> row the quota counts is written only after
    /// Claude answers — which is the right billing answer and also why nothing used to stand
    /// between an unreadable PDF and an unlimited retry loop. Each retry is a real billable
    /// extraction, and not a single call: the pass is re-sent on a timeout and a low-confidence
    /// result runs a second full pass over high-DPI images.
    /// </para>
    ///
    /// <para>
    /// Two is deliberate. One transient Claude blip is absorbed and the analyst gets their retry;
    /// the second failure closes the door until a genuinely different file is uploaded. The budget
    /// is counted per FILE (all non-deleted documents sharing a <c>FileHashSHA256</c>), so
    /// re-uploading the same bytes does not reset it.
    /// </para>
    ///
    /// <para>
    /// Zero or negative disables the guard entirely — every trigger is allowed through. That is
    /// the escape hatch if the counter ever misbehaves in production, and it matches the fail-open
    /// posture <c>BillingQuotaFilter</c> already takes: over-serving is recoverable, refusing work
    /// someone paid for is not.
    /// </para>
    /// </summary>
    public int MaxExtractionAttemptsPerDocument { get; init; } = 2;
}
