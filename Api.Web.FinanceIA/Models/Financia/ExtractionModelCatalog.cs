// Project : Api.Web.FinanceIA
// Created : 2026-11-01

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One Claude model the balance EXTRACTION can be configured to call
/// (<c>Anthropic:ExtractionModel</c>), together with everything the request needs to be built
/// correctly for it.
///
/// <para>
/// THE PER-MODEL FIELDS ARE NOT DECORATION — the same lesson as <see cref="ChatModel"/>.
/// Claude Sonnet 5 and Sonnet 5.5 reject a non-default sampling parameter with HTTP 400, and the
/// <c>temperature 0</c> Sonnet 4.6 has always been sent is non-default; both run ADAPTIVE THINKING
/// by default (no <c>thinking</c> field needed), and <c>max_tokens</c> caps the thinking and the JSON
/// TOGETHER, so a cap sized for Sonnet 4.6's answer alone would be spent before the JSON was finished.
/// </para>
/// </summary>
/// <param name="Id">The exact Anthropic model id sent on the wire. Complete as written: no date suffix.</param>
/// <param name="MaxTokens">Output ceiling for one extraction pass — thinking plus JSON, where the model thinks.</param>
/// <param name="Temperature">
/// Value for the payload's <c>temperature</c> field, or null when the model rejects the field and it
/// must be OMITTED (not sent as null).
/// </param>
/// <param name="SupportsEffort">
/// True when <c>Anthropic:ExtractionEffort</c> may be sent as <c>output_config.effort</c> for this
/// model. False for Sonnet 4.6 on purpose: it would accept the field, but the default payload must stay
/// byte-identical to the one this project has always sent.
/// </param>
/// <param name="TimeoutSeconds">
/// How long ONE attempt may take before it counts as timed out (and is retried, as today). Overridable
/// with <c>Anthropic:ExtractionTimeoutSeconds</c>.
/// </param>
public sealed record ExtractionModelSpec(
    string   Id,
    int      MaxTokens,
    decimal? Temperature,
    bool     SupportsEffort,
    int      TimeoutSeconds);

/// <summary>
/// The ONLY place an extraction model id and its request differences live. An id outside this
/// catalogue is never sent: <c>ExtractionModelSelection</c> logs an Error and falls back to
/// <see cref="Default"/>, so a typo in an App Setting degrades to today's behaviour instead of
/// failing every analysis. To add a model: an entry here, its price in <see cref="ClaudeModelPricing"/>
/// (a test enforces it), and a run of the model-comparison harness before any client uses it.
/// </summary>
public static class ExtractionModelCatalog
{
    /// <summary>
    /// The model extraction has always used, and still its default: temperature 0, max_tokens 16000,
    /// no output_config, 120 s per attempt. ClaudeExtractionPayloadTests pins its payload; it differs
    /// from the one sent when the model was a constant ONLY in max_tokens.
    /// <para>
    /// 16000, not the original 8000 (2026-11-03): complete Argentine balances with full notes already
    /// answered in 7.4k–8.4k tokens and a 10-page one was cut off at 8000 on every run. A balance that
    /// finishes earlier costs exactly the same — only the tokens produced are billed. A 16000-token
    /// answer takes about 480 s, beyond this spec's 120 s, so operations raise
    /// <c>Anthropic:ExtractionTimeoutSeconds</c> rather than this constant.
    /// </para>
    /// </summary>
    public static readonly ExtractionModelSpec Sonnet46 = new(
        Id:             "claude-sonnet-4-6",
        MaxTokens:      16000,
        Temperature:    0m,
        SupportsEffort: false,
        TimeoutSeconds: 120);

    /// <summary>
    /// The current Sonnet. No temperature (HTTP 400), adaptive thinking on by default at effort
    /// <c>high</c>. 32000 tokens: the JSON alone took up to ~8.4k Sonnet 4.6 tokens, the 5.x tokenizer
    /// emits roughly 30% more for the same text (~11k), and thinking shares the cap. 300 s per attempt, because a long thinking pass does not finish
    /// in the 120 s that bounds a Sonnet 4.6 answer.
    /// </summary>
    public static readonly ExtractionModelSpec Sonnet55 = new(
        Id:             "claude-sonnet-5-5",
        MaxTokens:      32000,
        Temperature:    null,
        SupportsEffort: true,
        TimeoutSeconds: 300);

    /// <summary>The previous Sonnet (legacy): the same request surface as Sonnet 5.5 for this call.</summary>
    public static readonly ExtractionModelSpec Sonnet5 = new(
        Id:             "claude-sonnet-5",
        MaxTokens:      32000,
        Temperature:    null,
        SupportsEffort: true,
        TimeoutSeconds: 300);

    /// <summary>The model used when <c>Anthropic:ExtractionModel</c> is unset, blank or unknown.</summary>
    public static readonly ExtractionModelSpec Default = Sonnet46;

    /// <summary>Every supported extraction model.</summary>
    public static readonly IReadOnlyList<ExtractionModelSpec> All = [Sonnet46, Sonnet55, Sonnet5];

    /// <summary>
    /// The values <c>output_config.effort</c> accepts on the models that support it, lower-case as
    /// sent on the wire.
    /// </summary>
    public static readonly IReadOnlyList<string> ValidEfforts = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>Lowest per-attempt timeout <c>Anthropic:ExtractionTimeoutSeconds</c> may set.</summary>
    public const int MinTimeoutSeconds = 30;

    /// <summary>Highest per-attempt timeout <c>Anthropic:ExtractionTimeoutSeconds</c> may set.</summary>
    public const int MaxTimeoutSeconds = 900;

    /// <summary>
    /// The highest <c>max_tokens</c> the one max_tokens ESCALATION may send
    /// (<see cref="Api.Web.FinanceIA.Servicios.ExtractionModelSelection.EscalateForMaxTokens"/>): a pass
    /// that stops at its spec's cap is re-sent once with the cap doubled, up to this value.
    /// </summary>
    public const int MaxEscalatedTokens = 32000;

    /// <summary>
    /// Share of a pass's <c>max_tokens</c> at which a SUCCESSFUL pass is logged as near the cap: the early
    /// warning that the spec's cap is about to stop being enough for real balances.
    /// </summary>
    public const decimal NearCapWarningRatio = 0.75m;

    /// <summary>
    /// True when <paramref name="outputTokens"/> reached <see cref="NearCapWarningRatio"/> of
    /// <paramref name="maxTokens"/>. False for a non-positive cap.
    /// </summary>
    public static bool IsNearCap(int outputTokens, int maxTokens)
        => maxTokens > 0 && outputTokens >= NearCapWarningRatio * maxTokens;

    /// <summary>
    /// Finds a model by id, case-insensitively and ignoring surrounding whitespace, or null when the
    /// catalogue has no such model.
    /// </summary>
    public static ExtractionModelSpec? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var trimmed = id.Trim();

        foreach (var spec in All)
        {
            if (string.Equals(spec.Id, trimmed, StringComparison.OrdinalIgnoreCase))
                return spec;
        }

        return null;
    }

    /// <summary>
    /// The canonical (lower-case) effort for <paramref name="value"/>, or null when it is not one of
    /// <see cref="ValidEfforts"/>.
    /// </summary>
    public static string? NormalizeEffort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();

        foreach (var effort in ValidEfforts)
        {
            if (string.Equals(effort, trimmed, StringComparison.OrdinalIgnoreCase))
                return effort;
        }

        return null;
    }
}
