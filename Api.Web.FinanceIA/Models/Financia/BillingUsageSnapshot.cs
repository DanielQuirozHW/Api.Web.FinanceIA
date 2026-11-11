// Project : Api.Web.FinanceIA
// Created : 2026-08-31

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// What one tenant has consumed against its plan in the current UTC calendar month.
///
/// <para>
/// Produced by <c>IBillingUsageService</c> and used by BOTH the enforcement gate and the
/// GET /api/billing/usage endpoint, deliberately: the number the Portal shows an analyst and the
/// number that refuses their next analysis have to be the same number, or the product looks
/// broken at exactly the moment it says no.
/// </para>
///
/// <para>
/// Failure is tracked per source rather than as one flag, because "we do not know the ceiling",
/// "we do not know the analysis count" and "we do not know the token count" are separate
/// failures with separate blast radii. ApiCallLogs is the likeliest of the three to have a bad
/// day — see BillingUsageService — and a bad day there must NOT switch off analysis enforcement.
/// </para>
/// </summary>
public sealed record BillingUsageSnapshot
{
    /// <summary>Display label of the plan in force. Never a lookup key.</summary>
    public required string PlanName { get; init; }

    /// <summary>First instant of the metered month, UTC and inclusive.</summary>
    public required DateTime PeriodStart { get; init; }

    /// <summary>First instant of the NEXT month, UTC and exclusive.</summary>
    public required DateTime PeriodEnd { get; init; }

    /// <summary>How the plan limits were obtained. Enforcement must fail open on ReadFailed.</summary>
    public required BillingPlanSource PlanSource { get; init; }

    /// <summary>Which table the chat-token figure was measured from.</summary>
    public required ChatTokenSource ChatTokensSource { get; init; }

    /// <summary>The analysis count could not be read; <see cref="AnalysesUsed"/> is not a fact.</summary>
    public required bool AnalysesReadFailed { get; init; }

    /// <summary>The chat-token sum could not be read; <see cref="ChatTokensUsed"/> is not a fact.</summary>
    public required bool ChatTokensReadFailed { get; init; }

    /// <summary>Analyses created this month, whatever their outcome.</summary>
    public required int AnalysesUsed { get; init; }

    /// <summary>Analyses the plan allows per month.</summary>
    public required int AnalysesLimit { get; init; }

    /// <summary>Chat tokens (input + output) spent this month.</summary>
    public required long ChatTokensUsed { get; init; }

    /// <summary>Chat tokens the plan allows per month.</summary>
    public required long ChatTokensLimit { get; init; }

    /// <summary>
    /// Claude tokens a SINGLE chat conversation's context may occupy.
    ///
    /// <para>
    /// A DENOMINATOR ONLY, and the one figure here with no "used" counterpart — deliberately.
    /// Everything else on this record is a monthly pool with a running total; this is a ceiling on
    /// one prompt at one moment, so there is nothing to accumulate over the period and it gets no
    /// Remaining or Percent helper. What a given conversation currently occupies is a property of
    /// that conversation, not of the tenant's month, and belongs to whatever measures it.
    /// </para>
    ///
    /// <para>
    /// It rides on this record anyway because it comes from the same plan resolution as the two
    /// limits above, so a caller that has the snapshot has the whole entitlement. Its trustworthiness
    /// is <see cref="PlanSource"/>'s, the same as <see cref="AnalysesLimit"/>'s.
    /// </para>
    /// </summary>
    public required int ChatContextBudgetTokens { get; init; }

    /// <summary>
    /// Ids of the Claude chat models the tenant may use, ordered by the catalogue and never empty.
    ///
    /// <para>
    /// The only member here that is not a NUMBER, and it is on this record for the same reason
    /// <see cref="ChatContextBudgetTokens"/> is: it comes from the same plan resolution, so a caller
    /// holding the snapshot holds the whole entitlement and does not need the SuperAdmin write
    /// endpoint to find out what the analyst's model picker should offer. Its trustworthiness is
    /// <see cref="PlanSource"/>'s, like every limit above.
    /// </para>
    ///
    /// <para>
    /// It has no "used" counterpart and never will: it is a set of capabilities, not an allowance,
    /// and calling a model does not consume the right to call it again.
    /// </para>
    /// </summary>
    public required IReadOnlyList<string> ChatEnabledModels { get; init; }

    /// <summary>
    /// Analyses still available. Clamped at zero: a tenant that somehow ran past its limit is
    /// out of quota, not owed a negative allowance.
    /// </summary>
    public int AnalysesRemaining => AnalysesLimit - AnalysesUsed > 0 ? AnalysesLimit - AnalysesUsed : 0;

    /// <summary>Chat tokens still available, clamped at zero for the same reason.</summary>
    public long ChatTokensRemaining => ChatTokensLimit - ChatTokensUsed > 0 ? ChatTokensLimit - ChatTokensUsed : 0;

    /// <summary>Percentage of the analysis allowance consumed, 2dp. Uncapped above 100.</summary>
    public decimal AnalysesPercent => Percent(AnalysesUsed, AnalysesLimit);

    /// <summary>Percentage of the chat-token allowance consumed, 2dp. Uncapped above 100.</summary>
    public decimal ChatTokensPercent => Percent(ChatTokensUsed, ChatTokensLimit);

    /// <summary>
    /// The fail-open predicate for the ANALYSIS gate: either the ceiling or the count is
    /// untrustworthy. Ask this — never re-derive it at a call site, or the two gates will drift.
    /// </summary>
    public bool AnalysisQuotaIsUnreliable => PlanSource == BillingPlanSource.ReadFailed || AnalysesReadFailed;

    /// <summary>The fail-open predicate for the CHAT gate.</summary>
    public bool ChatQuotaIsUnreliable => PlanSource == BillingPlanSource.ReadFailed || ChatTokensReadFailed;

    /// <summary>
    /// True when any part of this snapshot is a fallback rather than a fact, so a client should
    /// not present it as the tenant's entitlement.
    /// </summary>
    public bool IsDegraded => AnalysisQuotaIsUnreliable || ChatQuotaIsUnreliable;

    /// <summary>
    /// A stable machine string naming what could not be read, or null when everything could.
    /// Deliberately not Spanish prose: the Portal owns the copy, this owns the fact.
    /// </summary>
    public string? DegradedReason => (PlanSource == BillingPlanSource.ReadFailed, AnalysesReadFailed, ChatTokensReadFailed) switch
    {
        (true,  _,     _)     => "plan_read_failed",
        (false, true,  true)  => "analyses_and_chat_tokens_read_failed",
        (false, true,  false) => "analyses_read_failed",
        (false, false, true)  => "chat_tokens_read_failed",
        _                     => null
    };

    /// <summary>
    /// Percentage used, guarded against a non-positive limit. A zero or negative limit would be
    /// bad data rather than an infinitely small allowance, and dividing by it would throw inside
    /// a property the Portal reads on every poll, so it reports 0 instead.
    /// </summary>
    private static decimal Percent(long used, long limit)
    {
        if (limit <= 0) return 0m;

        return Math.Round(used * 100m / limit, 2, MidpointRounding.AwayFromZero);
    }
}
