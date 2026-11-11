// Project : Api.Web.FinanceIA
// Created : 2026-08-31

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// API shape of <see cref="BillingUsageSnapshot"/>, returned by GET /api/billing/usage.
///
/// <para>
/// A separate record rather than serializing the snapshot directly, for one concrete reason: the
/// snapshot carries enums, and no JsonStringEnumConverter is registered on this API, so they
/// would reach the Portal as bare integers. Property names go out camelCase — the app never sets
/// PropertyNamingPolicy, so AddControllers()'s JsonSerializerDefaults.Web applies — and the
/// registered UtcDateTimeConverter renders the two timestamps as UTC.
/// </para>
/// </summary>
public sealed record BillingUsageResponse
{
    /// <summary>Display label of the plan in force, e.g. "100 análisis".</summary>
    public required string PlanName { get; init; }

    /// <summary>First instant of the metered month, UTC and inclusive.</summary>
    public required DateTime PeriodStart { get; init; }

    /// <summary>First instant of the next month, UTC and exclusive.</summary>
    public required DateTime PeriodEnd { get; init; }

    /// <summary>Analyses created this month, whatever their outcome.</summary>
    public required int AnalysesUsed { get; init; }

    /// <summary>Analyses the plan allows per month.</summary>
    public required int AnalysesLimit { get; init; }

    /// <summary>Analyses still available, never below zero.</summary>
    public required int AnalysesRemaining { get; init; }

    /// <summary>Percentage of the analysis allowance consumed, 2dp. May exceed 100.</summary>
    public required decimal AnalysesPercent { get; init; }

    /// <summary>Chat tokens (input + output) spent this month.</summary>
    public required long ChatTokensUsed { get; init; }

    /// <summary>Chat tokens the plan allows per month.</summary>
    public required long ChatTokensLimit { get; init; }

    /// <summary>Chat tokens still available, never below zero.</summary>
    public required long ChatTokensRemaining { get; init; }

    /// <summary>Percentage of the chat-token allowance consumed, 2dp. May exceed 100.</summary>
    public required decimal ChatTokensPercent { get; init; }

    /// <summary>
    /// Claude tokens a SINGLE chat conversation's context may occupy — the whole a client renders a
    /// per-conversation usage chart against.
    ///
    /// <para>
    /// It arrives with no "used" companion because it is not a monthly pool: nothing accumulates
    /// against it over the period, so this endpoint has no numerator to report. What one
    /// conversation currently occupies is measured where that conversation is built.
    /// </para>
    ///
    /// <para>
    /// Readable by any authenticated tenant user; only setting it is SuperAdmin. NOTHING ENFORCES
    /// IT YET — no chat turn is refused or trimmed because of this number today.
    /// </para>
    /// </summary>
    public required int ChatContextBudgetTokens { get; init; }

    /// <summary>
    /// Ids of the Claude chat models this tenant may use — what a client populates the analyst's
    /// per-conversation model selector from. Ordered cheapest-first by the server's catalogue, and
    /// never empty.
    ///
    /// <para>
    /// Which of them to render pre-selected is <see cref="DefaultChatModel"/>, not this list's first
    /// entry — the two differ, and picking the first one here is how a client ends up offering the
    /// cheapest model rather than the product default. The first entry is only what the SERVER falls
    /// back to for a turn that names no model at all. Sending an id outside this list to
    /// POST /api/analyses/{resultId}/chat is safe but pointless: it is not an error, it is simply
    /// replaced by that first entry.
    /// </para>
    ///
    /// <para>
    /// Readable by any authenticated tenant user; only setting it is SuperAdmin. Unlike the figures
    /// above it, nothing is metered against it — it is a capability set, not an allowance.
    /// </para>
    /// </summary>
    public required IReadOnlyList<string> ChatEnabledModels { get; init; }

    /// <summary>
    /// The chat model id a client should render PRE-SELECTED in the analyst's model selector.
    /// Always one of <see cref="ChatEnabledModels"/>, so it can never name an option the selector
    /// does not offer.
    ///
    /// <para>
    /// It is the product default (<c>ChatModelCatalog.Default</c>) whenever the tenant is entitled to
    /// it, and otherwise the first model the tenant IS entitled to. It is stated explicitly because
    /// it is NOT <c>ChatEnabledModels[0]</c>: that list is ordered cheapest-first, so a client
    /// inferring the default from position lands on the cheap model instead of the intended one.
    /// </para>
    ///
    /// <para>
    /// A client that pre-selects this MUST send it back as <c>ChatRequest.Model</c> on every turn.
    /// This field states the intended default; it does not change the server's own fallback, which is
    /// still <c>ChatEnabledModels[0]</c> for a turn that names no model.
    /// </para>
    /// </summary>
    public required string DefaultChatModel { get; init; }

    /// <summary>
    /// Which table the chat figure was measured from: "ApiCallLogs" or "ChatMessages".
    /// Exposed because a zero means different things per source, and a client showing an analyst
    /// "0 tokens used" should be able to tell a quiet month from an unrecorded one.
    /// </summary>
    public required string ChatTokensSource { get; init; }

    /// <summary>
    /// Whether exceeding <see cref="ChatTokensLimit"/> actually rejects a chat turn right now.
    /// False while the per-plan token figures are still placeholders, so a client can meter
    /// without warning an analyst about a wall that is not there yet.
    /// </summary>
    public required bool ChatTokensEnforced { get; init; }

    /// <summary>
    /// True when any figure here is a fallback rather than a fact. The affected quota is not
    /// enforced while this is set, so a client should show the numbers with a caveat rather than
    /// present them as the tenant's entitlement — and should not blank the widget, which is why
    /// the endpoint still returns 200.
    /// </summary>
    public required bool IsDegraded { get; init; }

    /// <summary>
    /// Which read failed, as a stable machine string — "plan_read_failed",
    /// "analyses_read_failed", "chat_tokens_read_failed",
    /// "analyses_and_chat_tokens_read_failed" — or null when nothing failed. Not Spanish prose:
    /// the Portal owns the wording, this owns the fact.
    /// </summary>
    public string? DegradedReason { get; init; }

    /// <summary>
    /// The tenant's CURRENT risk-level cutoffs on the motor scale (0–100): score &lt;= low → Low,
    /// &lt;= medium → Medium, &lt;= high → High, above → Critical. Scoring configuration, not a billing
    /// figure — it rides here because this is the tenant read every role can make (see the endpoint).
    ///
    /// <para>
    /// A FALLBACK, not the authority. An analysis whose <c>scoreBreakdown</c> carries
    /// <c>levelCutoffs</c> must be labelled with THOSE — they are what that score was classified
    /// with, even after the tenant edited its config. Use these only for rows scored before the key
    /// existed. The level badge itself always comes from the stored level, never from a score and
    /// these numbers.
    /// </para>
    ///
    /// <para>
    /// Null when they could not be read. A client then renders no range rather than a hardcoded one;
    /// <see cref="IsDegraded"/> is unaffected, since it speaks for the quota figures only.
    /// </para>
    /// </summary>
    public ScoreLevelCutoffs? LevelCutoffs { get; init; }
}
